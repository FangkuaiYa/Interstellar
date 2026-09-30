package main

// End-to-end protocol tests. Every assertion here is a contract with
// Network/ServerConnection.cs (and Game/PublicLobbyWindow.cs) — if one of these
// changes, the plugin silently stops working, because its parser is strict and
// mostly silent on failure.

import (
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"
)

func testCfg() *config {
	return &config{
		listen:       "127.0.0.1:0",
		pingInterval: 25 * time.Second,
		pingTimeout:  10 * time.Second,
		maxClients:   0,
		maxLobbies:   50,
		ratePerSec:   1000,
		rateBurst:    1000,
	}
}

func startTestServer(t *testing.T, cfg *config) (*httptest.Server, *server) {
	t.Helper()
	s := newServer(cfg, log.New(io.Discard, "", 0))
	mux := http.NewServeMux()
	mux.HandleFunc("/socket.io/", s.handleSocket)
	mux.HandleFunc("/", s.handleStatus)
	hs := httptest.NewServer(mux)
	t.Cleanup(func() {
		hs.Close()
		s.shutdown()
	})
	return hs, s
}

// readEvent parses one "42[...]" frame.
func readEvent(t *testing.T, c *testWS) (string, []json.RawMessage) {
	t.Helper()
	raw := c.readExpect()
	if !strings.HasPrefix(raw, "42") {
		t.Fatalf("expected a socket.io event frame, got %q", raw)
	}
	var args []json.RawMessage
	if err := json.Unmarshal([]byte(raw[2:]), &args); err != nil {
		t.Fatalf("event json: %v (%q)", err, raw)
	}
	if len(args) == 0 {
		t.Fatalf("event with no name: %q", raw)
	}
	var name string
	if err := json.Unmarshal(args[0], &name); err != nil {
		t.Fatalf("event name: %v (%q)", err, raw)
	}
	return name, args[1:]
}

func expectJSON(t *testing.T, got json.RawMessage, want string) {
	t.Helper()
	var a, b any
	if err := json.Unmarshal(got, &a); err != nil {
		t.Fatalf("got invalid json %q: %v", got, err)
	}
	if err := json.Unmarshal([]byte(want), &b); err != nil {
		t.Fatalf("test expectation invalid: %v", err)
	}
	if fmt.Sprint(a) != fmt.Sprint(b) {
		t.Fatalf("payload mismatch:\n  got  %s\n  want %s", got, want)
	}
}

func expectSilence(t *testing.T, c *testWS, d time.Duration) {
	t.Helper()
	if msg, err := c.readWithin(d); err == nil {
		t.Fatalf("expected no further frames, got %q", msg)
	}
}

// drainUntil reads frames until one carries the wanted event name, ignoring
// everything else (roster updates, join announcements) that may be in flight.
func drainUntil(t *testing.T, c *testWS, want string, d time.Duration) {
	t.Helper()
	deadline := time.Now().Add(d)
	for time.Now().Before(deadline) {
		msg, err := c.readWithin(time.Until(deadline))
		if err != nil {
			t.Fatalf("drain %q: no such frame before timeout (last read err=%v)", want, err)
		}
		if !strings.HasPrefix(msg, "42") {
			continue
		}
		var arr []json.RawMessage
		if json.Unmarshal([]byte(msg[2:]), &arr) != nil || len(arr) == 0 {
			continue
		}
		var name string
		if json.Unmarshal(arr[0], &name) != nil {
			continue
		}
		if name == want {
			return
		}
	}
	t.Fatalf("drain %q: timed out", want)
}

// ---------------------------------------------------------------------------

// The plugin's HandleEngineIOMessage looks only at data[0]. If the open packet
// and "40" ever share a frame, the client never becomes "connected" and never
// joins — so the split is checked first, on its own.
func TestHandshakeIsTwoFrames(t *testing.T) {
	srv, _ := startTestServer(t, testCfg())
	c := dialWS(t, srv.URL)
	defer c.close()

	sid := c.handshake()
	if len(sid) < 10 {
		t.Fatalf("sid too short: %q", sid)
	}
}

func TestJoinGivesRosterAndAnnouncesToRoom(t *testing.T) {
	srv, s := startTestServer(t, testCfg())
	a := dialWS(t, srv.URL)
	defer a.close()
	aSid := a.handshake()

	// First member of an empty room: roster is an empty OBJECT (the client
	// rejects anything that is not json object — OnSetClients).
	a.emit("id", 1, 101)
	a.emit("join", "ROOM1", 1, 101, false)
	name, args := readEvent(t, a)
	if name != "setClients" {
		t.Fatalf("first frame after join must be setClients, got %q", name)
	}
	expectJSON(t, args[0], `{}`)

	b := dialWS(t, srv.URL)
	defer b.close()
	bSid := b.handshake()
	b.joinFrom(2, 202, "ROOM1")

	// The joiner learns the existing member from the roster…
	name, args = readEvent(t, b)
	if name != "setClients" {
		t.Fatalf("joiner must get setClients, got %q", name)
	}
	if bSid == aSid {
		t.Fatalf("sids must be unique")
	}
	rosterRaw := string(args[0])
	expectJSON(t, args[0], fmt.Sprintf(`{%q:{"clientId":101,"playerId":1}}`, aSid))
	// The roster must not advertise the requester's own sid back at it.
	if strings.Contains(rosterRaw, bSid) {
		t.Fatalf("roster should not be self-referential: %s", rosterRaw)
	}

	// …and the existing member hears a plain join event for the newcomer.
	name, args = readEvent(t, a)
	if name != "join" {
		t.Fatalf("existing member must get a join event, got %q", name)
	}
	if string(args[0]) != fmt.Sprintf("%q", bSid) {
		t.Fatalf("join sid = %s, want %q", args[0], bSid)
	}
	expectJSON(t, args[1], `{"clientId":202,"playerId":2}`)

	s.mu.Lock()
	rooms := len(s.rooms)
	s.mu.Unlock()
	if rooms != 1 {
		t.Fatalf("expected 1 room, got %d", rooms)
	}
}

// The whole point of this server: relay the P2P candidate/key exchange with the
// exact shape OnSignal expects ("from" is mandatory, it resolves peers by it).
func TestSignalRelayWireFormat(t *testing.T) {
	srv, _ := startTestServer(t, testCfg())
	a := dialWS(t, srv.URL)
	defer a.close()
	aSid := a.handshake()
	a.joinFrom(1, 101, "ROOM2")

	readEvent(t, a) // setClients

	b := dialWS(t, srv.URL)
	defer b.close()
	bSid := b.handshake()
	b.joinFrom(2, 202, "ROOM2")
	readEvent(t, b) // setClients
	readEvent(t, a) // join

	payload := "P2P:" + strings.Repeat("Abc123+/=", 8)
	b.signal(aSid, payload)

	name, args := readEvent(t, a)
	if name != "signal" {
		t.Fatalf("expected signal, got %q", name)
	}
	expectJSON(t, args[0], fmt.Sprintf(`{"data":%q,"from":%q,"to":%q}`, payload, bSid, aSid))

	// …and the reverse direction works the same way.
	a.signal(bSid, "HB:")
	name, args = readEvent(t, b)
	if name != "signal" {
		t.Fatalf("expected signal, got %q", name)
	}
	expectJSON(t, args[0], fmt.Sprintf(`{"data":"HB:","from":%q,"to":%q}`, aSid, bSid))
}

// A sid from another room must not be reachable — otherwise a guessed sid lets
// strangers inject HELLOs into somebody else's punch.
func TestSignalIsScopedToRoom(t *testing.T) {
	srv, _ := startTestServer(t, testCfg())
	a := dialWS(t, srv.URL)
	defer a.close()
	aSid := a.handshake()
	a.joinFrom(1, 101, "ROOM_A")
	readEvent(t, a)

	x := dialWS(t, srv.URL)
	defer x.close()
	xSid := x.handshake()
	x.joinFrom(9, 909, "ROOM_B")
	readEvent(t, x)

	// x tries to signal a sid it has no business talking to.
	x.signal(aSid, "P2P:hijack")
	expectSilence(t, a, 500*time.Millisecond)

	// Signalling an unknown sid in your own room is dropped too.
	x.signal("does-not-exist", "P2P:probe")
	expectSilence(t, x, 300*time.Millisecond)

	if xSid == aSid {
		t.Fatalf("sids must differ")
	}
}

// VAD arrives as a bare bool and has to leave carrying the sender's clientId.
func TestVADWrapsClientId(t *testing.T) {
	srv, _ := startTestServer(t, testCfg())
	a := dialWS(t, srv.URL)
	defer a.close()
	a.handshake()
	a.joinFrom(1, 101, "ROOM3")
	readEvent(t, a)

	b := dialWS(t, srv.URL)
	defer b.close()
	b.handshake()
	b.joinFrom(2, 202, "ROOM3")
	readEvent(t, b)
	readEvent(t, a)

	b.emit("VAD", false)
	name, args := readEvent(t, a)
	if name != "VAD" {
		t.Fatalf("expected VAD, got %q", name)
	}
	expectJSON(t, args[0], `{"activity":false,"client":{"clientId":202}}`)

	b.emit("VAD", true)
	_, args = readEvent(t, a)
	expectJSON(t, args[0], `{"activity":true,"client":{"clientId":202}}`)
}

// When a socket dies, the rest of the room is handed a fresh roster without it.
// That roster is what makes ServerConnection drop the dead sid — without it,
// peers keep aiming signals at a socket nobody reads.
func TestDisconnectPrunesRoster(t *testing.T) {
	srv, _ := startTestServer(t, testCfg())
	a := dialWS(t, srv.URL)
	defer a.close()
	a.handshake()
	a.joinFrom(1, 101, "ROOM4")
	readEvent(t, a)

	b := dialWS(t, srv.URL)
	bSid := b.handshake()
	b.joinFrom(2, 202, "ROOM4")
	readEvent(t, b)
	readEvent(t, a)

	b.close()

	deadline := time.Now().Add(3 * time.Second)
	for {
		name, args := readEvent(t, a)
		if name != "setClients" {
			continue
		}
		var roster map[string]any
		if err := json.Unmarshal(args[0], &roster); err != nil {
			t.Fatalf("roster json: %v", err)
		}
		if _, present := roster[bSid]; present {
			t.Fatalf("roster still contains the departed sid: %s", args[0])
		}
		if len(roster) != 0 {
			t.Fatalf("roster should be empty after the only peer left: %s", args[0])
		}
		break
	}
	if time.Now().After(deadline) {
		t.Fatalf("no pruning roster arrived")
	}
}

func TestClientPingIsAnsweredWithPong(t *testing.T) {
	srv, _ := startTestServer(t, testCfg())
	c := dialWS(t, srv.URL)
	defer c.close()
	c.handshake()

	c.write("2")
	msg, err := c.readWithin(2 * time.Second)
	if err != nil {
		t.Fatalf("no pong: %v", err)
	}
	if msg != "3" {
		t.Fatalf("expected \"3\", got %q", msg)
	}
}

// The lobby browser in Game/PublicLobbyWindow.cs opens its own socket, asks for
// the list, and resolves a lobby id back to a room code via a socket.io ack.
func TestLobbyRegistry(t *testing.T) {
	srv, s := startTestServer(t, testCfg())

	watcher := dialWS(t, srv.URL)
	defer watcher.close()
	watcher.handshake()
	watcher.emit("lobbybrowser", true)

	name, args := readEvent(t, watcher)
	if name != "new_lobbies" {
		t.Fatalf("expected new_lobbies snapshot, got %q", name)
	}
	expectJSON(t, args[0], `[]`)

	// Host publishes a lobby (Game/PublicLobbyWindow + ServerConnection.Emit).
	host := dialWS(t, srv.URL)
	defer host.close()
	host.handshake()
	host.emit("lobby", "QWERTY", map[string]any{
		"title": "测试房间", "host": "Player1", "current_players": 3,
		"max_players": 15, "language": "zh_CN", "mods": "none",
		"isPublic": true, "isPublic2": true, "server": "custom", "gameState": 0,
	})

	name, args = readEvent(t, watcher)
	if name != "update_lobby" {
		t.Fatalf("expected update_lobby, got %q", name)
	}
	var l struct {
		Id      int    `json:"id"`
		Code    string `json:"code"`
		Title   string `json:"title"`
		Players int    `json:"current_players"`
	}
	if err := json.Unmarshal(args[0], &l); err != nil {
		t.Fatalf("lobby json: %v", err)
	}
	if l.Code != "QWERTY" || l.Title != "测试房间" || l.Players != 3 {
		t.Fatalf("lobby payload wrong: %+v", l)
	}
	if l.Id <= 0 {
		t.Fatalf("server must assign a positive lobby id, got %d", l.Id)
	}

	// The browser copies the room code via join_lobby + ack.
	watcher.write(`421["join_lobby",` + fmt.Sprint(l.Id) + `]`)

	// Read the ack: must be 3<ackId>[0,"QWERTY"] (socket.io ack packet).
	ack := watcher.readExpect()
	if !strings.HasPrefix(ack, "31[0,\"QWERTY\"]") {
		t.Fatalf("join_lobby ack wrong: %q", ack)
	}

	// A re-publish keeps the same id.
	host.emit("lobby", "QWERTY", map[string]any{
		"title": "测试房间", "host": "Player1", "current_players": 5,
		"max_players": 15, "language": "zh_CN", "mods": "none",
		"isPublic": true, "server": "custom", "gameState": 1,
	})
	name, args = readEvent(t, watcher)
	if name != "update_lobby" {
		t.Fatalf("expected update_lobby, got %q", name)
	}
	var l2 struct {
		Id      int `json:"id"`
		Players int `json:"current_players"`
	}
	json.Unmarshal(args[0], &l2)
	if l2.Id != l.Id {
		t.Fatalf("lobby id changed across updates: %d -> %d", l.Id, l2.Id)
	}
	if l2.Players != 5 {
		t.Fatalf("update not applied: %+v", l2)
	}

	// Host goes away -> the lobby must not linger in the public list.
	host.close()
	deadline := time.Now().Add(3 * time.Second)
	removed := false
	for time.Now().Before(deadline) && !removed {
		msg, err := watcher.readWithin(2 * time.Second)
		if err != nil {
			continue
		}
		if !strings.HasPrefix(msg, "42") {
			continue
		}
		var arr []json.RawMessage
		if json.Unmarshal([]byte(msg[2:]), &arr) != nil || len(arr) < 2 {
			continue
		}
		var ev string
		json.Unmarshal(arr[0], &ev)
		if ev == "remove_lobby" {
			var id int
			json.Unmarshal(arr[1], &id)
			if id != l.Id {
				t.Fatalf("remove_lobby id = %d, want %d", id, l.Id)
			}
			removed = true
		}
	}
	if !removed {
		t.Fatalf("lobby survived its publisher's disconnect")
	}

	s.mu.Lock()
	remaining := len(s.lobbies)
	s.mu.Unlock()
	if remaining != 0 {
		t.Fatalf("lobby registry not cleaned up: %d left", remaining)
	}
}

func TestRemoveLobbyByCode(t *testing.T) {
	srv, _ := startTestServer(t, testCfg())
	w := dialWS(t, srv.URL)
	defer w.close()
	w.handshake()
	w.emit("lobbybrowser", true)
	readEvent(t, w) // new_lobbies

	h := dialWS(t, srv.URL)
	defer h.close()
	h.handshake()
	h.emit("lobby", "ABCDEF", map[string]any{"title": "t", "host": "h", "isPublic": true})
	readEvent(t, w) // update_lobby

	h.emit("remove_lobby", "ABCDEF")
	name, args := readEvent(t, w)
	if name != "remove_lobby" {
		t.Fatalf("expected remove_lobby, got %q", name)
	}
	var id int
	if err := json.Unmarshal(args[0], &id); err != nil || id <= 0 {
		t.Fatalf("remove_lobby must carry a numeric id, got %s", args[0])
	}
}

// Abuse protection: a socket that floods is disconnected instead of being
// allowed to burn the whole room's relay budget — and the room keeps working.
func TestRateLimitClosesFloodingClient(t *testing.T) {
	cfg := testCfg()
	cfg.ratePerSec = 50
	cfg.rateBurst = 20
	srv, _ := startTestServer(t, cfg)

	flood := dialWS(t, srv.URL)
	defer flood.close()
	flood.handshake()
	flood.joinFrom(1, 101, "FLOOD")

	other := dialWS(t, srv.URL)
	defer other.close()
	other.handshake()
	other.joinFrom(2, 202, "FLOOD")

	witness := dialWS(t, srv.URL)
	defer witness.close()
	witness.handshake()
	witness.joinFrom(3, 303, "FLOOD")
	drainUntil(t, witness, "setClients", 3*time.Second)

	// Burst far past the 20 message allowance. The server is expected to close
	// the socket mid-flood, so write errors here are the success path.
	for i := 0; i < 200; i++ {
		if err := flood.signalTry("does-not-exist", "HB:"); err != nil {
			break
		}
	}

	// The flooding socket must be dropped…
	deadline := time.Now().Add(3 * time.Second)
	closed := false
	for time.Now().Before(deadline) && !closed {
		if _, err := flood.readWithin(500 * time.Millisecond); err != nil {
			closed = true
		}
	}
	if !closed {
		t.Fatalf("flooding client was not disconnected")
	}

	// …and the other members still relay: VAD from `other` reaches `witness`
	// (along the way they also get the roster update pruning the flooder).
	other.emit("VAD", true)
	drainUntil(t, witness, "VAD", 3*time.Second)
}

// The message bucket has to stay loose enough for a big relay room (peers x
// frames/sec), which also makes it loose for a flooder — so bandwidth gets its own
// guard. A client pushing fat payloads is closed while its message count stays
// comfortably under the message limit, and the room around it keeps relaying.
func TestByteBudgetClosesFatClient(t *testing.T) {
	cfg := testCfg()
	cfg.maxBPS = 4000 // 4 KB/s sustained, 8 KB burst
	srv, _ := startTestServer(t, cfg)

	fat := dialWS(t, srv.URL)
	defer fat.close()
	fat.handshake()
	fat.joinFrom(1, 101, "FAT")

	other := dialWS(t, srv.URL)
	defer other.close()
	other.handshake()
	other.joinFrom(2, 202, "FAT")

	witness := dialWS(t, srv.URL)
	defer witness.close()
	witness.handshake()
	witness.joinFrom(3, 303, "FAT")
	drainUntil(t, witness, "setClients", 3*time.Second)

	// Two 6KB frames already blow past the 8KB burst, while four messages are
	// nothing against the 1000/s message budget — only bytes can trip here.
	payload := strings.Repeat("A", 6000)
	for i := 0; i < 4; i++ {
		if err := fat.signalTry("does-not-exist", payload); err != nil {
			break
		}
	}

	deadline := time.Now().Add(3 * time.Second)
	closed := false
	for time.Now().Before(deadline) && !closed {
		if _, err := fat.readWithin(500 * time.Millisecond); err != nil {
			closed = true
		}
	}
	if !closed {
		t.Fatalf("client over the byte budget was not disconnected")
	}

	// The room survives: VAD from `other` still reaches `witness`.
	other.emit("VAD", true)
	drainUntil(t, witness, "VAD", 3*time.Second)
}

// Server-initiated pings keep clients alive; silent ones get reaped.
func TestHousekeepPingAndReap(t *testing.T) {
	cfg := testCfg()
	cfg.pingInterval = 300 * time.Millisecond
	cfg.pingTimeout = 300 * time.Millisecond
	srv, s := startTestServer(t, cfg)

	alive := dialWS(t, srv.URL)
	defer alive.close()
	aliveSid := alive.handshake()

	dead := dialWS(t, srv.URL)
	defer dead.close()
	deadSid := dead.handshake()
	dead.noPong = true // refuse to answer, like a stalled NAT mapping

	stop := make(chan struct{})
	var wg sync.WaitGroup
	wg.Add(1)
	go func() {
		defer wg.Done()
		for {
			select {
			case <-stop:
				return
			default:
				s.housekeep()
				time.Sleep(50 * time.Millisecond)
			}
		}
	}()

	// The live client must see a server ping.
	msg, err := alive.readWithin(2 * time.Second)
	if err != nil || msg != "2" {
		t.Fatalf("expected server ping \"2\", got %q (err=%v)", msg, err)
	}

	// After pingInterval+pingTimeout with no pong, the silent one is gone but
	// the responsive one survives.
	deadline := time.Now().Add(4 * time.Second)
	for time.Now().Before(deadline) {
		s.mu.Lock()
		_, deadGone := s.clients[deadSid]
		_, alivePresent := s.clients[aliveSid]
		s.mu.Unlock()
		if !deadGone && alivePresent {
			time.Sleep(100 * time.Millisecond)
			continue
		}
		if deadGone && alivePresent {
			break
		}
		if !alivePresent {
			close(stop)
			wg.Wait()
			t.Fatalf("the responsive client was reaped")
		}
	}
	close(stop)
	wg.Wait()

	s.mu.Lock()
	_, deadGone := s.clients[deadSid]
	_, alivePresent := s.clients[aliveSid]
	s.mu.Unlock()
	if !deadGone {
		t.Fatalf("silent client was not reaped")
	}
	if !alivePresent {
		t.Fatalf("responsive client did not survive")
	}
}

func TestStatusEndpoint(t *testing.T) {
	srv, _ := startTestServer(t, testCfg())

	// Wire counters only mean anything after real frames, so put a client on the
	// wire first — the operator reads these to size the box. The handshake itself
	// is server-pushed; the join is the first thing the client actually sends.
	c := dialWS(t, srv.URL)
	defer c.close()
	c.handshake()
	c.joinFrom(1, 101, "STAT")

	resp, err := http.Get(srv.URL + "/")
	if err != nil {
		t.Fatalf("get: %v", err)
	}
	defer resp.Body.Close()
	body, _ := io.ReadAll(resp.Body)
	if resp.StatusCode != 200 {
		t.Fatalf("status %d", resp.StatusCode)
	}
	var st map[string]any
	if err := json.Unmarshal(body, &st); err != nil {
		t.Fatalf("status json: %v (%s)", err, body)
	}
	if st["ok"] != true {
		t.Fatalf("status not ok: %s", body)
	}
	if in, ok := st["bytes_in"].(float64); !ok || in <= 0 {
		t.Fatalf("bytes_in missing or zero: %s", body)
	}
	if _, ok := st["bytes_out"].(float64); !ok {
		t.Fatalf("bytes_out missing: %s", body)
	}
	if _, ok := st["max_clients"].(float64); !ok {
		t.Fatalf("max_clients missing: %s", body)
	}
}

// A plain HTTP GET to the socket.io path must not hang on a polling transport
// this server does not implement.
func TestNonUpgradeIsRejected(t *testing.T) {
	srv, _ := startTestServer(t, testCfg())
	resp, err := http.Get(srv.URL + "/socket.io/?EIO=3&transport=polling")
	if err != nil {
		t.Fatalf("get: %v", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusUpgradeRequired {
		t.Fatalf("expected 426, got %d", resp.StatusCode)
	}
}
