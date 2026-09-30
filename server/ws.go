package main

// Minimal RFC 6455 WebSocket server — handshake, framing, ping/pong, close.
//
// Deliberately hand-written instead of pulling gorilla/nhooyr: the target is a
// single static binary with zero dependencies, and the plugin only ever needs
// text frames. Two behaviours here matter to the client specifically:
//
//   - every engine.io packet is written as exactly ONE unfragmented frame.
//     Interstellar's WebSocket wrapper (Network/WebSocket.cs) reassembles
//     fragments, but ServerConnection.HandleEngineIOMessage only looks at the
//     FIRST byte of what it receives, so the Engine.IO open packet ("0{...}")
//     and the socket.io connect packet ("40") must never share a frame — they
//     are written as two separate frames below.
//   - frames are never larger than maxMsg, keeping a slow or hostile peer from
//     buffering an unbounded message.

import (
	"bufio"
	"crypto/sha1"
	"encoding/base64"
	"encoding/binary"
	"errors"
	"io"
	"net"
	"net/http"
	"strings"
	"sync"
	"time"
)

const wsGUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"

const (
	opContinuation = 0x0
	opText         = 0x1
	opBinary       = 0x2
	opClose        = 0x8
	opPing         = 0x9
	opPong         = 0xA
)

const (
	defaultMaxMessage = 128 << 10 // 128 KiB
	defaultWriteWait  = 10 * time.Second
)

var (
	errNotUpgrade    = errors.New("ws: not a websocket upgrade")
	errMissingKey    = errors.New("ws: missing Sec-WebSocket-Key")
	errBadFrame      = errors.New("ws: malformed frame")
	errTooLarge      = errors.New("ws: message exceeds limit")
	errBinary        = errors.New("ws: binary frames are not used by this protocol")
	errNoHijack      = errors.New("ws: connection hijacking unsupported")
)

type wsConn struct {
	c   net.Conn
	br  *bufio.Reader
	wmu sync.Mutex

	maxMsg  int64
	writeTo time.Duration
}

// acceptKey computes Sec-WebSocket-Accept per RFC 6455 §4.2.2.
func acceptKey(key string) string {
	h := sha1.New()
	io.WriteString(h, key)
	io.WriteString(h, wsGUID)
	return base64.StdEncoding.EncodeToString(h.Sum(nil))
}

// upgrade performs the server side of the opening handshake and takes the
// connection over from net/http.
func upgrade(w http.ResponseWriter, r *http.Request) (*wsConn, error) {
	if !strings.EqualFold(r.Header.Get("Upgrade"), "websocket") {
		http.Error(w, "websocket upgrade required", http.StatusBadRequest)
		return nil, errNotUpgrade
	}
	key := r.Header.Get("Sec-WebSocket-Key")
	if key == "" {
		http.Error(w, "Sec-WebSocket-Key required", http.StatusBadRequest)
		return nil, errMissingKey
	}
	hj, ok := w.(http.Hijacker)
	if !ok {
		http.Error(w, "hijacking unsupported", http.StatusInternalServerError)
		return nil, errNoHijack
	}
	conn, bufrw, err := hj.Hijack()
	if err != nil {
		return nil, err
	}
	resp := "HTTP/1.1 101 Switching Protocols\r\n" +
		"Upgrade: websocket\r\n" +
		"Connection: Upgrade\r\n" +
		"Sec-WebSocket-Accept: " + acceptKey(key) + "\r\n\r\n"
	if _, err := bufrw.WriteString(resp); err != nil {
		conn.Close()
		return nil, err
	}
	if err := bufrw.Flush(); err != nil {
		conn.Close()
		return nil, err
	}
	if tcp, ok := conn.(*net.TCPConn); ok {
		_ = tcp.SetNoDelay(true)
	}
	// bufrw.Reader may already hold the client's first frame — keep reading from it.
	return &wsConn{c: conn, br: bufrw.Reader, maxMsg: defaultMaxMessage, writeTo: defaultWriteWait}, nil
}

func (w *wsConn) readFrame() (fin bool, opcode byte, payload []byte, err error) {
	var hdr [2]byte
	if _, err = io.ReadFull(w.br, hdr[:]); err != nil {
		return false, 0, nil, err
	}
	fin = hdr[0]&0x80 != 0
	if hdr[0]&0x70 != 0 {
		return false, 0, nil, errBadFrame // RSV bits must stay clear (no extensions)
	}
	opcode = hdr[0] & 0x0F
	masked := hdr[1]&0x80 != 0

	length := int64(hdr[1] & 0x7F)
	switch length {
	case 126:
		var ext [2]byte
		if _, err = io.ReadFull(w.br, ext[:]); err != nil {
			return false, 0, nil, err
		}
		length = int64(binary.BigEndian.Uint16(ext[:]))
	case 127:
		var ext [8]byte
		if _, err = io.ReadFull(w.br, ext[:]); err != nil {
			return false, 0, nil, err
		}
		length = int64(binary.BigEndian.Uint64(ext[:]))
		if length < 0 {
			return false, 0, nil, errBadFrame
		}
	}
	if length > w.maxMsg {
		return false, 0, nil, errTooLarge
	}

	var maskKey [4]byte
	if masked {
		if _, err = io.ReadFull(w.br, maskKey[:]); err != nil {
			return false, 0, nil, err
		}
	}
	payload = make([]byte, length)
	if length > 0 {
		if _, err = io.ReadFull(w.br, payload); err != nil {
			return false, 0, nil, err
		}
		if masked {
			for i := range payload {
				payload[i] ^= maskKey[i&3]
			}
		}
	}
	return fin, opcode, payload, nil
}

// readMessage returns the next complete data message, transparently handling
// fragmentation. Ping/pong are answered inline; a close frame is echoed back.
func (w *wsConn) readMessage() (opcode byte, payload []byte, err error) {
	var msg []byte
	started := false
	for {
		fin, op, data, ferr := w.readFrame()
		if ferr != nil {
			return 0, nil, ferr
		}
		switch op {
		case opClose:
			_ = w.write(opClose, data)
			return 0, nil, io.EOF
		case opPing:
			if err := w.write(opPong, data); err != nil {
				return 0, nil, err
			}
			continue
		case opPong:
			continue
		}

		if !started {
			if op == opContinuation {
				return 0, nil, errBadFrame
			}
			if op == opBinary {
				return 0, nil, errBinary
			}
			opcode = op
			started = true
		} else if op != opContinuation {
			return 0, nil, errBadFrame // control/data interleaving rules
		}

		if int64(len(msg)+len(data)) > w.maxMsg {
			return 0, nil, errTooLarge
		}
		msg = append(msg, data...)
		if fin {
			return opcode, msg, nil
		}
	}
}

// write sends one complete, unfragmented frame. Server frames are never masked.
func (w *wsConn) write(opcode byte, payload []byte) error {
	w.wmu.Lock()
	defer w.wmu.Unlock()

	n := len(payload)
	var hdr []byte
	switch {
	case n < 126:
		hdr = []byte{0x80 | opcode, byte(n)}
	case n <= 0xFFFF:
		hdr = []byte{0x80 | opcode, 126, 0, 0}
		binary.BigEndian.PutUint16(hdr[2:], uint16(n))
	default:
		hdr = make([]byte, 10)
		hdr[0] = 0x80 | opcode
		hdr[1] = 127
		binary.BigEndian.PutUint64(hdr[2:], uint64(n))
	}

	buf := make([]byte, 0, len(hdr)+n)
	buf = append(buf, hdr...)
	buf = append(buf, payload...)

	_ = w.c.SetWriteDeadline(time.Now().Add(w.writeTo))
	_, err := w.c.Write(buf)
	return err
}

func (w *wsConn) closeNow() {
	_ = w.c.Close()
}
