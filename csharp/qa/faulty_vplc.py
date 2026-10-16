#!/usr/bin/env python3
"""QA-only deterministic FINS/UDP virtual PLC: silently change end-station barcode.

Fixed 127.0.0.1:19600 binding, loopback clients only, 5,000 simulated DM words.
This is NOT an industrial FINS driver and can never connect to external equipment.
"""
import socket
import struct


PORT = 19600


def barcode_words(text):
    raw = text.encode('ascii').ljust(20, b'\0')
    return list(struct.unpack('>10H', raw))


def respond(request, memory):
    if (len(request) < 18 or request[0] != 0x80 or request[10] != 1 or
            request[11] not in (1, 2) or request[12] != 0x82 or request[15] != 0):
        return b'', None
    address, count = struct.unpack_from('>HH', request, 13)[0], struct.unpack_from('>H', request, 16)[0]
    write = request[11] == 2
    if (count == 0 or count > 64 or address + count > len(memory) or
            len(request) != 18 + (count * 2 if write else 0)):
        return b'', None
    reply = bytearray(request[:12])
    reply[0] = 0xC0
    reply[4], reply[7] = request[7], request[4]
    reply.extend(b'\x00\x00')
    if write:
        memory[address:address + count] = struct.unpack_from('>' + 'H' * count, request, 18)
    else:
        reply.extend(struct.pack('>' + 'H' * count, *memory[address:address + count]))
    return bytes(reply), ('write' if write else 'read', address, count)


def main():
    memory = [0] * 5000
    memory[1999] = 1
    memory[2000:2010] = barcode_words('SIM-0001')
    flipped = False
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.bind(('127.0.0.1', PORT))
        print('QA_VPLC_LISTEN 127.0.0.1:19600 SIM_ONLY barcode-flip', flush=True)
        while True:
            payload, remote = sock.recvfrom(2048)
            if remote[0] != '127.0.0.1':
                continue
            answer, action = respond(payload, memory)
            if not answer:
                continue
            sock.sendto(answer, remote)
            if not flipped and action == ('read', 2000, 10):
                # Respond to the first loading-station scan with SIM-0001, then
                # change memory BEFORE the independent end-barcode read.
                memory[2000:2010] = barcode_words('SIM-9999')
                flipped = True
                print('QA_BARCODE_FLIPPED before end read SIM-0001 -> SIM-9999', flush=True)
            if action and action[0] == 'write' and action[1] in (1016, 2099):
                print(f'QA_UNEXPECTED_FINAL_WRITE address=D{action[1]} value={memory[action[1]]}', flush=True)


if __name__ == '__main__':
    main()
