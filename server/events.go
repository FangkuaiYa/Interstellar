package main

// Engine.IO / socket.io dispatch and the room + lobby registries.
// See main.go for the full protocol table and the two wire details that the
// plugin's parser depends on.

import (
	"encoding/json"
	"strings"
	"time"
)

type lobby struct {
	Id             int    `json:"id"`
	Code           string `json:"code"`
	Title          string `json:"title"`
	Host           string `json:"host"`
	CurrentPlayers int    `json:"current_players"`
	MaxPlayers     int    `json:"max_players"`
	Language       string `json:"language"`
	Mods           string `json:"mods"`
	IsPublic       bool   `json:"isPublic"`
	Server         string `json:"server"`
	GameState      int    `json:"gameState"`
	Owner          string `json:"-"` // sid that published it (cleared on disconnect)
}

// ---------------------------------------------------------------------------
// engine.io
// ---------------------------------------------------------------------------

func (s *server) onEngine(c *client, data string) {
	if data == "" {
		return
	}

	// Bookkeeping + rate limit under the lock; dispatch outside it.
	now := time.Now()
	s.mu.Lock()
	c.lastRecv = now
	c.tokens = clampFloat(c.tokens+now.Sub(c.lastFill).Seconds()*s.cfg.ratePerSec, 0, s.cfg.rateBurst)
	c.lastFill = now
	c.tokens--
	over := c.tokens < 0

	// Second, independent guard: bytes. Message rate has to stay loose enough for
	// a 60-player relay room (peers x frames/sec), which also loosens it for a
	// flooder — so bandwidth gets its own bucket. Media frames are ~460B, a room
	// of that size peaks around 700KB/s per connection, and the default budget is
	// several times that, so only a deliberate upload flood ever trips it.
	overBytes := false
	if s.cfg.maxBPS > 0 {
		c.byteTokens = clampFloat(c.byteTokens+now.Sub(c.byteFill).Seconds()*s.cfg.maxBPS, 0, s.cfg.maxBPS*2)
		c.byteFill = now
		c.byteTokens -= float64(len(data))
		overBytes = c.byteTokens < 0
	}
	s.mu.Unlock()

	if over {
		s.warnf("[!] sid=%s exceeded %.0f msg/s — closing", c.sid, s.cfg.ratePerSec)
		s.dropClient(c)
		return
	}
	if overBytes {
		s.warnf("[!] sid=%s exceeded %.0f B/s inbound — closing", c.sid, s.cfg.maxBPS)
		s.dropClient(c)
		return
	}

	switch data[0] {
	case '0':
		// Engine.IO open is server-to-client only in this protocol.
	case '2': // client ping
		if !c.enqueue("3") {
			s.dropClient(c)
		}
	case '3': // pong — lastRecv above already proves liveness
	case '4':
		if len(data) > 1 {
			s.onSio(c, data[1:])
		}
	}
}

func (s *server) onSio(c *client, payload string) {
	if payload == "" {
		return
	}
	switch payload[0] {
	case '0':
		// Namespace connect requested by the client. The plugin waits for ours
		// instead, but a stock socket.io client sends this — answer it.
		if !c.enqueue("40") {
			s.dropClient(c)
		}
	case '1':
		s.dropClient(c)
	case '2':
		body := payload[1:]
		ackID := ""
		if i := strings.IndexByte(body, '['); i > 0 {
			ackID = body[:i]
			body = body[i:]
		}
		if !strings.HasPrefix(body, "[") {
			s.logf("[?] sid=%s malformed event frame: %.80q", c.sid, payload)
			return
		}
		var args []json.RawMessage
		if err := json.Unmarshal([]byte(body), &args); err != nil {
			s.logf("[?] sid=%s bad event json: %v", c.sid, err)
			return
		}
		if len(args) == 0 {
			return
		}
		var name string
		if err := json.Unmarshal(args[0], &name); err != nil || name == "" {
			return
		}
		s.onEvent(c, name, args[1:], ackID)
	case '3':
		// Client-side ack: nothing in this protocol asks for one.
	case '4':
		s.logf("[?] sid=%s socket.io error packet", c.sid)
	}
}

// ---------------------------------------------------------------------------
// socket.io events
// ---------------------------------------------------------------------------

func (s *server) onEvent(c *client, name string, args []json.RawMessage, ackID string) {
	switch name {
	case "id":
		if len(args) < 2 {
			return
		}
		var pid, cid int
		if json.Unmarshal(args[0], &pid) != nil || json.Unmarshal(args[1], &cid) != nil {
			return
		}
		s.mu.Lock()
		c.playerID, c.clientID = pid, cid
		s.mu.Unlock()
		s.logf("[i] sid=%s identified pid=%d cid=%d", c.sid, pid, cid)

	case "join":
		s.handleJoin(c, args)

	case "leave":
		s.mu.Lock()
		s.leaveLocked(c)
		s.mu.Unlock()

	case "VAD":
		s.handleVAD(c, args)

	case "signal":
		s.handleSignal(c, args)

	case "lobbybrowser":
		s.handleLobbyBrowser(c, args)

	case "lobby":
		s.handleLobbyPublish(c, args)

	case "remove_lobby":
		s.handleLobbyRemove(c, args)

	case "join_lobby":
		s.handleJoinLobby(c, args, ackID)

	default:
		s.logf("[?] sid=%s unknown event %q", c.sid, name)
	}
}

// join: register the socket in a room, hand it the current roster, announce it.
func (s *server) handleJoin(c *client, args []json.RawMessage) {
	if len(args) < 3 {
		return
	}
	var room string
	var pid, cid int
	if json.Unmarshal(args[0], &room) != nil {
		return
	}
	json.Unmarshal(args[1], &pid)
	json.Unmarshal(args[2], &cid)
	room = strings.TrimSpace(room)
	if room == "" {
		return
	}

	s.mu.Lock()
	defer s.mu.Unlock()

	if c.room != "" && c.room != room {
		s.leaveLocked(c)
	}
	c.room, c.playerID, c.clientID, c.joined = room, pid, cid, true

	members := s.rooms[room]
	if members == nil {
		members = map[string]*client{}
		s.rooms[room] = members
	}
	members[c.sid] = c

	// The joiner learns pre-existing members ONLY from this roster — there is no
	// replay of their original "join" events (ServerConnection.OnSetClients).
	s.enqueueLocked(c, sioEvent("setClients", s.rosterLocked(room, c.sid)))

	// Everyone else hears the arrival as a normal join event.
	s.broadcastLocked(room, c.sid, sioEvent("join", c.sid,
		map[string]any{"playerId": pid, "clientId": cid}))

	s.logf("[+] join room=%s sid=%s pid=%d cid=%d members=%d", room, c.sid, pid, cid, len(members))
}

// VAD: the client sends a bare bool, everyone else needs the sender's clientId.
func (s *server) handleVAD(c *client, args []json.RawMessage) {
	if len(args) < 1 {
		return
	}
	var activity bool
	if err := json.Unmarshal(args[0], &activity); err != nil {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if c.room == "" {
		return
	}
	s.broadcastLocked(c.room, c.sid, sioEvent("VAD", map[string]any{
		"activity": activity,
		"client":   map[string]any{"clientId": c.clientID},
	}))
}

// signal: the only thing this server relays for P2P. Payloads are candidate
// lists, ephemeral public keys and the existing HB/RADIO/HOST/MSG markers —
// never audio.
func (s *server) handleSignal(c *client, args []json.RawMessage) {
	if len(args) < 1 {
		return
	}
	var in struct {
		To   string `json:"to"`
		Data string `json:"data"`
	}
	if err := json.Unmarshal(args[0], &in); err != nil {
		return
	}
	if in.To == "" || in.Data == "" {
		return
	}

	s.mu.Lock()
	defer s.mu.Unlock()
	if c.room == "" {
		return
	}
	target := s.rooms[c.room][in.To]
	if target == nil || target.sid == c.sid {
		// Aimed at a sid the room no longer has: normal during reconnects, and
		// the transport re-sends HELLOs on its own timer.
		return
	}
	s.enqueueLocked(target, sioEvent("signal",
		map[string]any{"from": c.sid, "to": target.sid, "data": in.Data}))
}

// ---------------------------------------------------------------------------
// public lobby registry (in-memory, best effort)
// ---------------------------------------------------------------------------

func (s *server) handleLobbyBrowser(c *client, args []json.RawMessage) {
	if len(args) < 1 {
		return
	}
	var on bool
	if err := json.Unmarshal(args[0], &on); err != nil {
		return
	}
	s.mu.Lock()
	if on {
		s.watchers[c.sid] = c
		c.watching = true
		// Full snapshot first; everything after this arrives as update_lobby.
		s.enqueueLocked(c, sioEvent("new_lobbies", s.sortedLobbiesLocked()))
	} else {
		delete(s.watchers, c.sid)
		c.watching = false
	}
	s.mu.Unlock()
	s.logf("[i] lobbybrowser sid=%s watching=%v", c.sid, on)
}

func (s *server) handleLobbyPublish(c *client, args []json.RawMessage) {
	if len(args) < 2 {
		return
	}
	var code string
	if json.Unmarshal(args[0], &code) != nil {
		return
	}
	code = strings.TrimSpace(code)
	if code == "" {
		return
	}

	l := &lobby{}
	if err := json.Unmarshal(args[1], l); err != nil {
		s.logf("[?] sid=%s bad lobby json: %v", c.sid, err)
		return
	}

	s.mu.Lock()
	defer s.mu.Unlock()

	if prev, ok := s.lobbies[code]; ok {
		l.Id = prev.Id
	} else {
		if s.cfg.maxLobbies > 0 && len(s.lobbies) >= s.cfg.maxLobbies {
			s.evictOldestLobbyLocked()
		}
		s.nextID++
		l.Id = s.nextID
	}
	l.Code, l.Owner = code, c.sid

	// A host has exactly one room: drop anything else this socket published so
	// it cannot outlive the socket as an orphaned entry.
	for other, existing := range s.lobbies {
		if existing.Owner == c.sid && other != code {
			s.broadcastWatchersLocked(sioEvent("remove_lobby", existing.Id))
			delete(s.lobbies, other)
		}
	}

	s.lobbies[code] = l
	c.lobbyCode = code
	s.broadcastWatchersLocked(sioEvent("update_lobby", l))
}

func (s *server) handleLobbyRemove(c *client, args []json.RawMessage) {
	if len(args) < 1 {
		return
	}
	var code string
	if json.Unmarshal(args[0], &code) != nil {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.removeLobbyLocked(strings.TrimSpace(code), c.sid)
}

// removeLobbyLocked drops a lobby, but only if `owner` still owns it — a
// host that migrated to another socket must not be able to delete the
// replacement's entry. owner == "" forces removal.
func (s *server) removeLobbyLocked(code, owner string) {
	l, ok := s.lobbies[code]
	if !ok {
		return
	}
	if owner != "" && l.Owner != owner {
		return
	}
	delete(s.lobbies, code)
	if l.Owner != "" {
		if oc := s.clients[l.Owner]; oc != nil && oc.lobbyCode == code {
			oc.lobbyCode = ""
		}
	}
	s.broadcastWatchersLocked(sioEvent("remove_lobby", l.Id))
	s.logf("[-] lobby removed code=%s id=%d", code, l.Id)
}

func (s *server) evictOldestLobbyLocked() {
	var oldest *lobby
	var oldestCode string
	for code, l := range s.lobbies {
		if oldest == nil || l.Id < oldest.Id {
			oldest, oldestCode = l, code
		}
	}
	if oldest == nil {
		return
	}
	delete(s.lobbies, oldestCode)
	s.broadcastWatchersLocked(sioEvent("remove_lobby", oldest.Id))
	s.warnf("[!] lobby limit %d reached — evicted id=%d code=%s", s.cfg.maxLobbies, oldest.Id, oldestCode)
}

// join_lobby ack: the client's lobby browser asks for the room code behind a
// listed lobby id and copies it to the clipboard.
// Wire form: 3<ackId>[0,"CODE"] on success, 3<ackId>[1,""] when unknown.
func (s *server) handleJoinLobby(c *client, args []json.RawMessage, ackID string) {
	if len(args) < 1 || ackID == "" {
		return
	}
	var id int
	if json.Unmarshal(args[0], &id) != nil {
		return
	}

	s.mu.Lock()
	var found *lobby
	for _, l := range s.lobbies {
		if l.Id == id {
			found = l
			break
		}
	}
	s.mu.Unlock()

	if found == nil {
		s.logf("[?] sid=%s join_lobby id=%d not found", c.sid, id)
		if !c.enqueue(sioEventAck(ackID, 1, "")) {
			s.dropClient(c)
		}
		return
	}
	if !c.enqueue(sioEventAck(ackID, 0, found.Code)) {
		s.dropClient(c)
	}
}

func sioEventAck(ackID string, state int, value string) string {
	b, err := json.Marshal([]any{state, value})
	if err != nil {
		return ""
	}
	return "3" + ackID + string(b)
}

// sendRosterLocked hands every remaining member its own view of the room:
// peers only, never self. The roster is authoritative — ServerConnection prunes
// anything missing from it, which is how peers stop aiming signals at a sid
// nobody reads any more.
func (s *server) sendRosterLocked(room string) {
	var dead []*client
	for sid, m := range s.rooms[room] {
		if !m.enqueue(sioEvent("setClients", s.rosterLocked(room, sid))) {
			dead = append(dead, m)
		}
	}
	for _, d := range dead {
		s.dropLocked(d)
	}
}

// leaveLocked removes the socket from its room.
func (s *server) leaveLocked(c *client) {
	if c.room == "" {
		return
	}
	room := c.room
	c.room = ""
	c.joined = false

	members := s.rooms[room]
	if members == nil {
		return
	}
	delete(members, c.sid)
	if len(members) == 0 {
		delete(s.rooms, room)
		s.logf("[-] room %s empty — closed", room)
		return
	}
	s.sendRosterLocked(room)
}
