package main

// Signalling server for the Interstellar P2P voice plugin.
//
// The plugin speaks the BetterCrewLink socket.io dialect over Engine.IO v3
// (EIO=3), websocket transport only. Media never touches this process — it
// rides UDP peer-to-peer — so everything here is small, synchronous and
// allocation-light: one goroutine per connection, one global mutex, no
// allocations on the hot path beyond the outbound JSON.
//
// Protocol reference (see Network/ServerConnection.cs, which is the authority):
//
//	client -> server : 42["id", playerId, clientId]
//	client -> server : 42["join", room, playerId, clientId, false]
//	client -> server : 42["VAD", bool]
//	client -> server : 42["signal", {"to": sid, "data": "..."}]
//	client -> server : 42["lobbybrowser", bool]
//	client -> server : 42["lobby", code, {...}] / 42["remove_lobby", code]
//	client -> server : 42<ack>["join_lobby", lobbyId]
//
//	server -> client : 0{"sid":...,"upgrades":[],"pingInterval":...,"pingTimeout":...}
//	server -> client : 40
//	server -> client : 42["setClients", {sid: {playerId, clientId}, ...}]
//	server -> client : 42["join", sid, {playerId, clientId}]
//	server -> client : 42["VAD", {"activity": bool, "client": {"clientId": n}}]
//	server -> client : 42["signal", {"from": sid, "to": sid, "data": "..."}]
//	server -> client : 42["new_lobbies", [...]] / 42["update_lobby", {...}] / 42["remove_lobby", id]
//	server -> client : 3<ack>[0, "ROOMCODE"]
//
// Two wire details are load-bearing and easy to break:
//
//  1. The Engine.IO open packet and the socket.io "40" MUST go out as two
//     separate websocket frames. ServerConnection.HandleEngineIOMessage only
//     inspects the first byte of a received frame, so a coalesced
//     "0{...}40" would leave the client waiting for a connect packet forever
//     (and it never sends one itself — see HandleOpen's comment).
//  2. "from" is mandatory on every relayed signal: the receiver resolves its
//     peer map by sid and drops anything from an unknown one.

import (
	"context"
	"crypto/rand"
	"encoding/base64"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"log"
	"math"
	"net"
	"net/http"
	"os"
	"os/signal"
	"sort"
	"strings"
	"sync"
	"sync/atomic"
	"syscall"
	"time"
)

const version = "1.0.0"

type config struct {
	listen       string
	pingInterval time.Duration
	pingTimeout  time.Duration
	maxClients   int
	maxLobbies   int
	ratePerSec   float64
	rateBurst    float64
	maxBPS       float64 // per-connection inbound bytes/second, 0 = off
	quiet        bool
	tlsCert      string
	tlsKey       string
}

type server struct {
	cfg *config
	log *log.Logger

	mu       sync.Mutex
	clients  map[string]*client            // sid -> connection
	rooms    map[string]map[string]*client // room code -> sid -> connection
	watchers map[string]*client            // sid -> sockets that asked for the lobby list
	lobbies  map[string]*lobby             // room code -> published lobby
	nextID   int

	// Wire volume, for the status endpoint: an operator replacing BCL servers
	// needs to see what a relay-heavy room actually costs before it costs money.
	bytesIn  atomic.Int64
	bytesOut atomic.Int64
	statAt   time.Time // last rate sample, guarded by mu
	statIn   int64
	statOut  int64
	inBPS    int64
	outBPS   int64

	started time.Time
}

// client is one websocket. Everything below `mu` is shared state; `send` and
// `closed` are owned by that client's two goroutines.
type client struct {
	sid  string
	ws   *wsConn
	send chan string
	srv  *server // set at creation; writePump reports wire bytes through it

	mu     sync.Mutex // guards `closed`+`dropped` against concurrent close paths
	closed chan struct{}
	once   sync.Once
	drop   bool

	// Shared state, guarded by server.mu.
	room      string
	playerID  int
	clientID  int
	joined    bool
	watching  bool
	lobbyCode string
	lastRecv  time.Time
	lastPing  time.Time
	tokens    float64
	lastFill  time.Time

	// Second bucket: inbound bytes (see onEngine).
	byteTokens float64
	byteFill   time.Time
}

func main() {
	cfg := &config{}
	env := func(key, def string) string {
		if v := os.Getenv(key); v != "" {
			return v
		}
		return def
	}

	flag.StringVar(&cfg.listen, "listen", env("SIGNAL_LISTEN", ":8090"), "listen address (env SIGNAL_LISTEN)")
	pingMs := flag.Int("ping-interval", 25000, "server ping interval, ms")
	timeoutMs := flag.Int("ping-timeout", 10000, "pong deadline before a connection is dropped, ms")
	// Deliberately small: this is the cost dial for someone replacing a BCL fleet,
	// and rooms are the unit of spend (a 60-player room is 60 connections).
	flag.IntVar(&cfg.maxClients, "max-clients", 128, "maximum simultaneous connections (0 = unlimited)")
	flag.IntVar(&cfg.maxLobbies, "max-lobbies", 200, "maximum published lobbies (0 = unlimited)")
	// Defaults sized for media relay in rooms well past 15 players: in Relay/Auto
	// mode every voice frame is one "signal" per peer (40ms = 25/s, 20ms = 50/s),
	// so 60 players at 20ms is ~2965 msg/s per connection. The original 150/400
	// was signalling-sized and cut a 15-person relay room off in seconds. A
	// message bucket loose enough for that can no longer be protective on its
	// own, which is what -max-bps is for.
	flag.Float64Var(&cfg.ratePerSec, "rate", 3500, "sustained messages/second per connection (must cover relayed media: peers x frames/sec)")
	flag.Float64Var(&cfg.rateBurst, "burst", 9000, "message burst allowance per connection (~3s of a full relay room)")
	flag.Float64Var(&cfg.maxBPS, "max-bps", 3_000_000, "per-connection inbound bytes/second (0 = unlimited)")
	flag.BoolVar(&cfg.quiet, "quiet", false, "only log warnings and errors")
	flag.StringVar(&cfg.tlsCert, "tls-cert", "", "certificate file (enables https/wss)")
	flag.StringVar(&cfg.tlsKey, "tls-key", "", "private key file")
	showVersion := flag.Bool("version", false, "print version and exit")
	flag.Parse()

	if *showVersion {
		fmt.Println("interstellar-signal", version)
		return
	}

	cfg.pingInterval = time.Duration(*pingMs) * time.Millisecond
	cfg.pingTimeout = time.Duration(*timeoutMs) * time.Millisecond
	if cfg.pingInterval < time.Second {
		cfg.pingInterval = time.Second
	}

	lg := log.New(os.Stdout, "", log.LstdFlags|log.Lmicroseconds)
	s := newServer(cfg, lg)

	mux := http.NewServeMux()
	// Longest-pattern wins: /socket.io/ takes precedence over the "/" catch-all.
	mux.HandleFunc("/socket.io/", s.handleSocket)
	mux.HandleFunc("/", s.handleStatus)

	srv := &http.Server{
		Addr:              cfg.listen,
		Handler:           mux,
		ReadHeaderTimeout: 10 * time.Second,
		// Hijacked connections are taken over by us; IdleTimeout only applies to
		// the status endpoint.
		IdleTimeout: 30 * time.Second,
		ErrorLog:    lg,
	}

	ln, err := net.Listen("tcp", cfg.listen)
	if err != nil {
		lg.Fatalf("[!] listen %s: %v", cfg.listen, err)
	}
	if cfg.tlsCert != "" && cfg.tlsKey != "" {
		lg.Printf("[i] interstellar-signal %s listening on https://%s (tls)", version, cfg.listen)
		go func() {
			if err := srv.ServeTLS(ln, cfg.tlsCert, cfg.tlsKey); err != nil && !errors.Is(err, http.ErrServerClosed) {
				lg.Printf("[!] serve: %v", err)
			}
		}()
	} else {
		lg.Printf("[i] interstellar-signal %s listening on ws://%s (no tls — point the plugin at http://…)", version, cfg.listen)
		go func() {
			if err := srv.Serve(ln); err != nil && !errors.Is(err, http.ErrServerClosed) {
				lg.Printf("[!] serve: %v", err)
			}
		}()
	}

	// Housekeeping: server-initiated engine.io pings + dead-peer reaping.
	stop := make(chan struct{})
	go func() {
		t := time.NewTicker(5 * time.Second)
		defer t.Stop()
		for {
			select {
			case <-t.C:
				s.housekeep()
			case <-stop:
				return
			}
		}
	}()

	sig := make(chan os.Signal, 1)
	signal.Notify(sig, syscall.SIGINT, syscall.SIGTERM)
	<-sig
	close(stop)

	lg.Println("[i] shutting down…")
	shutdownCtx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	_ = srv.Shutdown(shutdownCtx)
	s.shutdown()
}

func newServer(cfg *config, lg *log.Logger) *server {
	return &server{
		cfg:      cfg,
		log:      lg,
		clients:  map[string]*client{},
		rooms:    map[string]map[string]*client{},
		watchers: map[string]*client{},
		lobbies:  map[string]*lobby{},
		started:  time.Now(),
	}
}

func (s *server) logf(format string, a ...any) {
	if s.cfg.quiet {
		return
	}
	s.log.Printf(format, a...)
}

func (s *server) warnf(format string, a ...any) {
	s.log.Printf(format, a...)
}

// ---------------------------------------------------------------------------
// connection lifecycle
// ---------------------------------------------------------------------------

func randomSID() string {
	var b [15]byte
	if _, err := rand.Read(b[:]); err != nil {
		return fmt.Sprintf("%d", time.Now().UnixNano())
	}
	return base64.RawURLEncoding.EncodeToString(b[:])
}

func (s *server) handleSocket(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.Error(w, "GET websocket upgrade required", http.StatusMethodNotAllowed)
		return
	}
	if !strings.EqualFold(r.Header.Get("Upgrade"), "websocket") {
		// A plain GET to /socket.io/ is somebody poking with curl — answer helpfully
		// instead of hanging on the polling transport, which this server does not
		// implement (the plugin only ever opens websockets).
		w.Header().Set("Content-Type", "text/plain; charset=utf-8")
		w.WriteHeader(http.StatusUpgradeRequired)
		fmt.Fprintln(w, "websocket transport required: connect to /socket.io/?EIO=3&transport=websocket")
		return
	}

	if s.cfg.maxClients > 0 {
		s.mu.Lock()
		full := len(s.clients) >= s.cfg.maxClients
		s.mu.Unlock()
		if full {
			http.Error(w, "server full", http.StatusServiceUnavailable)
			s.warnf("[!] connection refused: max-clients %d reached", s.cfg.maxClients)
			return
		}
	}

	ws, err := upgrade(w, r)
	if err != nil {
		return
	}

	c := &client{
		sid:        randomSID(),
		ws:         ws,
		send:       make(chan string, 256),
		srv:        s,
		closed:     make(chan struct{}),
		tokens:     s.cfg.rateBurst,
		byteTokens: s.cfg.maxBPS * 2,
		lastRecv:   time.Now(),
		lastPing:   time.Now(),
		lastFill:   time.Now(),
		byteFill:   time.Now(),
	}

	s.mu.Lock()
	if s.cfg.maxClients > 0 && len(s.clients) >= s.cfg.maxClients {
		s.mu.Unlock()
		ws.closeNow()
		return
	}
	s.clients[c.sid] = c
	s.mu.Unlock()

	remote := r.RemoteAddr
	ua := r.Header.Get("User-Agent")
	s.logf("[+] sid=%s from=%s ua=%q", c.sid, remote, ua)

	// Engine.IO open and socket.io connect go out as two distinct frames — see
	// the file header. They are buffered by `send`, so ordering holds.
	if !c.enqueue(s.openPacketFor(c), "40") {
		ws.closeNow()
		return
	}

	go c.writePump()
	s.readPump(c)
	s.dropClient(c)
}

func (s *server) openPacketFor(c *client) string {
	return fmt.Sprintf(`0{"sid":%q,"upgrades":[],"pingInterval":%d,"pingTimeout":%d}`,
		c.sid, s.cfg.pingInterval.Milliseconds(), s.cfg.pingTimeout.Milliseconds())
}

// enqueue never blocks: a socket whose queue fills up is a socket nobody is
// reading any more, and dropping it lets the plugin reconnect cleanly instead
// of stalling the whole room behind one dead peer.
func (c *client) enqueue(msgs ...string) bool {
	for _, m := range msgs {
		select {
		case c.send <- m:
		case <-c.closed:
			return false
		default:
			return false
		}
	}
	return true
}

func (c *client) writePump() {
	for {
		select {
		case msg := <-c.send:
			if err := c.ws.write(opText, []byte(msg)); err != nil {
				c.ws.closeNow()
				return
			}
			c.srv.bytesOut.Add(int64(len(msg)))
		case <-c.closed:
			return
		}
	}
}

func (s *server) readPump(c *client) {
	for {
		op, payload, err := c.ws.readMessage()
		if err != nil {
			switch {
			case errors.Is(err, errTooLarge):
				s.warnf("[!] sid=%s message too large — closing", c.sid)
			case isEOF(err):
				// Normal disconnects (client closed, reset) are not worth a line.
			default:
				s.logf("[-] sid=%s read: %v", c.sid, err)
			}
			return
		}
		s.bytesIn.Add(int64(len(payload)))
		if op != opText {
			continue
		}
		s.onEngine(c, string(payload))
		if c.isClosed() {
			return
		}
	}
}

func isEOF(err error) bool {
	return errors.Is(err, net.ErrClosed) || errors.Is(err, os.ErrDeadlineExceeded) ||
		strings.Contains(err.Error(), "EOF") || strings.Contains(err.Error(), "closed")
}

func (c *client) isClosed() bool {
	select {
	case <-c.closed:
		return true
	default:
		return false
	}
}

// dropClient removes a connection from every index. Safe to call twice and
// safe to call while another path holds no lock.
func (s *server) dropClient(c *client) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.dropLocked(c)
}

// dropLocked assumes s.mu is held.
func (s *server) dropLocked(c *client) {
	if c.drop {
		return
	}
	c.drop = true

	_, known := s.clients[c.sid]
	room := c.room
	delete(s.clients, c.sid)
	delete(s.watchers, c.sid)
	s.leaveLocked(c)

	if c.lobbyCode != "" {
		s.removeLobbyLocked(c.lobbyCode, c.sid)
	}
	c.closeOnce()
	if known {
		s.logf("[-] sid=%s room=%q", c.sid, room)
	}
}

func (c *client) closeOnce() {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.once.Do(func() {
		close(c.closed)
		c.ws.closeNow()
	})
}

func (s *server) shutdown() {
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, c := range s.clients {
		s.dropLocked(c)
	}
}

// housekeep sends an engine.io ping so clients whose own ping timer is gated on
// a completed handshake still generate traffic, and reaps sockets that stopped
// answering.
func (s *server) housekeep() {
	now := time.Now()
	deadline := s.cfg.pingInterval + s.cfg.pingTimeout

	s.mu.Lock()
	var dead []*client
	for _, c := range s.clients {
		if now.Sub(c.lastRecv) > deadline {
			dead = append(dead, c)
			continue
		}
		if now.Sub(c.lastPing) >= s.cfg.pingInterval {
			c.lastPing = now
			if !c.enqueue("2") {
				dead = append(dead, c)
			}
		}
	}
	for _, c := range dead {
		s.logf("[-] sid=%s no pong for %s", c.sid, deadline)
		s.dropLocked(c)
	}
	s.mu.Unlock()
}

// ---------------------------------------------------------------------------
// status endpoint (also the health check)
// ---------------------------------------------------------------------------

func (s *server) handleStatus(w http.ResponseWriter, r *http.Request) {
	if r.URL.Path != "/" && r.URL.Path != "/healthz" {
		http.NotFound(w, r)
		return
	}
	s.mu.Lock()
	// Rate = average between the two most recent polls, so `curl` twice a minute
	// and you have a real traffic number, not a guess.
	now := time.Now()
	bi, bo := s.bytesIn.Load(), s.bytesOut.Load()
	if !s.statAt.IsZero() {
		if el := now.Sub(s.statAt).Seconds(); el >= 0.25 {
			s.inBPS = int64(float64(bi-s.statIn) / el)
			s.outBPS = int64(float64(bo-s.statOut) / el)
			s.statIn, s.statOut, s.statAt = bi, bo, now
		}
	} else {
		s.statIn, s.statOut, s.statAt = bi, bo, now
	}
	body, _ := json.Marshal(map[string]any{
		"ok":          true,
		"version":     version,
		"clients":     len(s.clients),
		"rooms":       len(s.rooms),
		"lobbies":     len(s.lobbies),
		"uptime_s":    int(time.Since(s.started).Seconds()),
		"bytes_in":    bi,
		"bytes_out":   bo,
		"in_bps":      s.inBPS,
		"out_bps":     s.outBPS,
		"max_clients": s.cfg.maxClients,
		"rate_limit":  s.cfg.ratePerSec,
		"max_bps":     int(s.cfg.maxBPS),
	})
	s.mu.Unlock()
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(http.StatusOK)
	w.Write(body)
}

// ---------------------------------------------------------------------------
// helpers shared by the event handlers (events.go)
// ---------------------------------------------------------------------------

// sioEvent renders a socket.io event packet: "42" followed by a JSON array
// whose first element is the event name.
func sioEvent(name string, args ...any) string {
	payload := make([]any, 0, len(args)+1)
	payload = append(payload, name)
	payload = append(payload, args...)
	b, err := json.Marshal(payload)
	if err != nil {
		return ""
	}
	return "42" + string(b)
}

// roster renders the setClients payload for a room: every member except `skip`.
func (s *server) rosterLocked(room string, skip string) map[string]any {
	members := s.rooms[room]
	out := make(map[string]any, len(members))
	for sid, m := range members {
		if sid == skip {
			continue
		}
		out[sid] = map[string]any{"playerId": m.playerID, "clientId": m.clientID}
	}
	return out
}

// broadcastLocked sends msg to every member of room except `skip`.
func (s *server) broadcastLocked(room, skip, msg string) {
	if msg == "" {
		return
	}
	var dead []*client
	for sid, m := range s.rooms[room] {
		if sid == skip {
			continue
		}
		if !m.enqueue(msg) {
			dead = append(dead, m)
		}
	}
	for _, d := range dead {
		s.dropLocked(d)
	}
}

// broadcastWatchersLocked sends msg to every socket watching the lobby list.
func (s *server) broadcastWatchersLocked(msg string) {
	if msg == "" {
		return
	}
	var dead []*client
	for _, c := range s.watchers {
		if !c.enqueue(msg) {
			dead = append(dead, c)
			continue
		}
	}
	for _, d := range dead {
		s.dropLocked(d)
	}
}

func (s *server) enqueueLocked(c *client, msg string) {
	if msg == "" || c == nil {
		return
	}
	if !c.enqueue(msg) {
		s.dropLocked(c)
	}
}

// sortedLobbies returns the lobby list in a stable order so clients diffing
// snapshots do not see gratuitous reshuffles.
func (s *server) sortedLobbiesLocked() []*lobby {
	out := make([]*lobby, 0, len(s.lobbies))
	for _, l := range s.lobbies {
		out = append(out, l)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Id < out[j].Id })
	return out
}

// clampFloat is a tiny helper for rate bookkeeping.
func clampFloat(v, lo, hi float64) float64 {
	return math.Max(lo, math.Min(hi, v))
}
