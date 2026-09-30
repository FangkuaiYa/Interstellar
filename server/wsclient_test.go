package main

// A tiny websocket client used only by the tests. It speaks enough RFC 6455 to
// drive the server the way the plugin does: unmasked frames from the server,
// masked frames from this side, one message per frame, text only.

import (
	"bufio"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/url"
	"strings"
	"testing"
	"time"
)

type testWS struct {
	t  *testing.T
	c  net.Conn
	br *bufio.Reader
	// noPong makes the client refuse to answer server pings, so the reaper can
	// be tested against a socket that has stopped responding.
	noPong bool
}

func dialWS(t *testing.T, baseURL string) *testWS {
	t.Helper()
	u, err := url.Parse(baseURL)
	if err != nil {
		t.Fatalf("parse url: %v", err)
	}
	conn, err := net.DialTimeout("tcp", u.Host, 3*time.Second)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}

	key := "dGhlIHNhbXBsZSBub25jZQ==" // the RFC 6455 example key
	path := "/socket.io/?EIO=3&transport=websocket"
	req := fmt.Sprintf("GET %s HTTP/1.1\r\nHost: %s\r\nUpgrade: websocket\r\n"+
		"Connection: Upgrade\r\nSec-WebSocket-Key: %s\r\nSec-WebSocket-Version: 13\r\n"+
		"User-Agent: BetterCrewLink/3.1.4 (win32)\r\nOrigin: http://localhost\r\n\r\n",
		path, u.Host, key)
	if _, err := conn.Write([]byte(req)); err != nil {
		t.Fatalf("write handshake: %v", err)
	}

	br := bufio.NewReader(conn)
	status, err := br.ReadString('\n')
	if err != nil {
		t.Fatalf("read status: %v", err)
	}
	if !strings.Contains(status, "101") {
		t.Fatalf("expected 101 Switching Protocols, got %q", strings.TrimSpace(status))
	}
	gotAccept := ""
	for {
		line, err := br.ReadString('\n')
		if err != nil {
			t.Fatalf("read header: %v", err)
		}
		line = strings.TrimRight(line, "\r\n")
		if line == "" {
			break
		}
		if i := strings.Index(line, ":"); i > 0 {
			if strings.EqualFold(strings.TrimSpace(line[:i]), "Sec-WebSocket-Accept") {
				gotAccept = strings.TrimSpace(line[i+1:])
			}
		}
	}
	// RFC 6455 appendix example: key+dGUID -> s3pPLMBiTxaQ9kYGzzhZRbK+xOo=
	if want := "s3pPLMBiTxaQ9kYGzzhZRbK+xOo="; gotAccept != want {
		t.Fatalf("Sec-WebSocket-Accept = %q, want %q", gotAccept, want)
	}

	return &testWS{t: t, c: conn, br: br}
}

// read returns the payload of the next complete text message. The caller owns
// the read deadline (readExpect / readWithin set it).
func (w *testWS) read() (string, error) {
	var msg []byte
	for {
		var hdr [2]byte
		if _, err := io.ReadFull(w.br, hdr[:]); err != nil {
			return "", err
		}
		fin := hdr[0]&0x80 != 0
		op := hdr[0] & 0x0F
		masked := hdr[1]&0x80 != 0
		length := int64(hdr[1] & 0x7F)
		switch length {
		case 126:
			var ext [2]byte
			if _, err := io.ReadFull(w.br, ext[:]); err != nil {
				return "", err
			}
			length = int64(binary.BigEndian.Uint16(ext[:]))
		case 127:
			var ext [8]byte
			if _, err := io.ReadFull(w.br, ext[:]); err != nil {
				return "", err
			}
			length = int64(binary.BigEndian.Uint64(ext[:]))
		}
		var mask [4]byte
		if masked {
			if _, err := io.ReadFull(w.br, mask[:]); err != nil {
				return "", err
			}
		}
		buf := make([]byte, length)
		if length > 0 {
			if _, err := io.ReadFull(w.br, buf); err != nil {
				return "", err
			}
			if masked {
				for i := range buf {
					buf[i] ^= mask[i&3]
				}
			}
		}
		switch op {
		case 0x9: // ping from server -> pong
			if !w.noPong {
				w.writeOp(0xA, buf)
			}
			continue
		case 0xA:
			continue
		case 0x8:
			return "", io.EOF
		}
		if op != 0x1 {
			return "", fmt.Errorf("unexpected opcode %d", op)
		}
		msg = append(msg, buf...)
		if fin {
			return string(msg), nil
		}
	}
}

// readExpect returns the next message or fails the test.
func (w *testWS) readExpect() string {
	w.t.Helper()
	_ = w.c.SetReadDeadline(time.Now().Add(3 * time.Second))
	s, err := w.read()
	if err != nil {
		w.t.Fatalf("read: %v", err)
	}
	return s
}

// readWithin waits up to d for a message; it returns an error on timeout.
func (w *testWS) readWithin(d time.Duration) (string, error) {
	_ = w.c.SetReadDeadline(time.Now().Add(d))
	return w.read()
}

func (w *testWS) write(text string) {
	w.t.Helper()
	if err := w.writeText(text); err != nil {
		w.t.Fatalf("write frame: %v", err)
	}
}

// writeText is the non-fatal variant: the rate-limit test deliberately writes
// into a connection the server is in the middle of closing.
func (w *testWS) writeText(text string) error {
	return w.writeOpErr(0x1, []byte(text))
}

func (w *testWS) writeOp(op byte, payload []byte) {
	w.t.Helper()
	if err := w.writeOpErr(op, payload); err != nil {
		w.t.Fatalf("write frame: %v", err)
	}
}

func (w *testWS) writeOpErr(op byte, payload []byte) error {
	n := len(payload)
	var hdr []byte
	switch {
	case n < 126:
		hdr = []byte{0x80 | op, 0x80 | byte(n)}
	case n <= 0xFFFF:
		hdr = []byte{0x80 | op, 0x80 | 126, 0, 0}
		binary.BigEndian.PutUint16(hdr[2:], uint16(n))
	default:
		hdr = make([]byte, 10)
		hdr[0] = 0x80 | op
		hdr[1] = 0x80 | 127
		binary.BigEndian.PutUint64(hdr[2:], uint64(n))
	}
	mask := [4]byte{0x11, 0x22, 0x33, 0x44}
	body := make([]byte, len(payload))
	for i := range payload {
		body[i] = payload[i] ^ mask[i&3]
	}
	buf := append(append(hdr, mask[:]...), body...)
	_, err := w.c.Write(buf)
	return err
}

func (w *testWS) close() {
	_ = w.c.Close()
}

// ---------------------------------------------------------------------------
// helpers that mirror the plugin's exact emits (Network/ServerConnection.cs)
// ---------------------------------------------------------------------------

// handshake performs the two-step open the plugin depends on and returns the
// server-assigned sid. Fails the test if the frames arrive coalesced, because
// ServerConnection.HandleEngineIOMessage only inspects the first byte.
func (w *testWS) handshake() string {
	w.t.Helper()
	open := w.readExpect()
	if !strings.HasPrefix(open, "0{") {
		w.t.Fatalf("first frame must be the engine.io open packet, got %q", open)
	}
	var payload struct {
		Sid          string `json:"sid"`
		PingInterval int    `json:"pingInterval"`
		PingTimeout  int    `json:"pingTimeout"`
		Upgrades     []any  `json:"upgrades"`
	}
	if err := json.Unmarshal([]byte(open[1:]), &payload); err != nil {
		w.t.Fatalf("open packet json: %v (%q)", err, open)
	}
	if payload.Sid == "" {
		w.t.Fatalf("open packet carries no sid: %q", open)
	}
	if payload.PingInterval <= 0 {
		w.t.Fatalf("open packet missing pingInterval: %q", open)
	}
	if payload.Upgrades == nil {
		w.t.Fatalf("open packet must declare upgrades: %q", open)
	}
	connect := w.readExpect()
	if connect != "40" {
		w.t.Fatalf("second frame must be exactly \"40\", got %q — the client would wait forever for it", connect)
	}
	return payload.Sid
}

func (w *testWS) emit(name string, args ...any) {
	w.t.Helper()
	payload := make([]any, 0, len(args)+1)
	payload = append(payload, name)
	payload = append(payload, args...)
	b, err := json.Marshal(payload)
	if err != nil {
		w.t.Fatalf("marshal emit: %v", err)
	}
	w.write("42" + string(b))
}

// joinFrom performs the plugin's DoJoin sequence verbatim.
func (w *testWS) joinFrom(playerID, clientID int, room string) {
	w.t.Helper()
	w.emit("id", playerID, clientID)
	w.emit("join", room, playerID, clientID, false)
}

func (w *testWS) signal(to, data string) {
	w.t.Helper()
	w.emit("signal", map[string]any{"to": to, "data": data})
}

// signalTry tolerates a write failure — used when the test is provoking the
// server into closing the connection.
func (w *testWS) signalTry(to, data string) error {
	b, err := json.Marshal([]any{"signal", map[string]any{"to": to, "data": data}})
	if err != nil {
		return err
	}
	return w.writeText("42" + string(b))
}
