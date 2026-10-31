import { createSocket, type Socket } from 'node:dgram';

/*
 * A minimal OSC 1.0 client over UDP for the Phase 8 golden path (docs/09-phase-8.md, step 5): messages with int,
 * float and string arguments, sent to Anode's OSC server, and the feedback it sends back to a subscribed port.
 * The address space is workstream D's (`docs/control/osc.md`); align the addresses there at integration.
 */

export type OscValue = number | string | { int: number };

function pad(buffer: Buffer): Buffer {
  const size = Math.ceil((buffer.length + 1) / 4) * 4;
  const out = Buffer.alloc(size);
  buffer.copy(out);
  return out;
}

function oscString(text: string): Buffer {
  return pad(Buffer.from(text, 'utf8'));
}

/** An OSC message: a plain number is a float32, `{ int }` an int32. */
export function encodeOsc(address: string, args: readonly OscValue[] = []): Buffer {
  const tags = ',' + args.map(a => (typeof a === 'string' ? 's' : typeof a === 'number' ? 'f' : 'i')).join('');
  const parts = [oscString(address), oscString(tags)];
  for (const arg of args) {
    if (typeof arg === 'string') {
      parts.push(oscString(arg));
    } else {
      const b = Buffer.alloc(4);
      if (typeof arg === 'number') {
        b.writeFloatBE(arg);
      } else {
        b.writeInt32BE(arg.int);
      }
      parts.push(b);
    }
  }
  return Buffer.concat(parts);
}

function readString(buffer: Buffer, offset: number): [string, number] {
  const end = buffer.indexOf(0, offset);
  const text = buffer.toString('utf8', offset, end);
  return [text, Math.ceil((end + 1) / 4) * 4];
}

/** Decodes one OSC message (bundles are not expected in feedback). */
export function decodeOsc(buffer: Buffer): { address: string; args: (number | string | boolean)[] } {
  const [address, afterAddress] = readString(buffer, 0);
  const [tags, afterTags] = readString(buffer, afterAddress);
  const args: (number | string | boolean)[] = [];
  let offset = afterTags;
  for (const tag of tags.slice(1)) {
    if (tag === 'f') {
      args.push(buffer.readFloatBE(offset));
      offset += 4;
    } else if (tag === 'i') {
      args.push(buffer.readInt32BE(offset));
      offset += 4;
    } else if (tag === 's') {
      const [text, next] = readString(buffer, offset);
      args.push(text);
      offset = next;
    } else if (tag === 'T' || tag === 'F') {
      args.push(tag === 'T');
    }
  }
  return { address, args };
}

/** A UDP client that sends to Anode's OSC port and collects what comes back on its own port. */
export class OscClient {
  readonly received: { address: string; args: (number | string | boolean)[] }[] = [];
  private constructor(
    private readonly socket: Socket,
    readonly port: number,
    private readonly host: string,
    private readonly serverPort: number
  ) {
    socket.on('message', data => this.received.push(decodeOsc(data)));
  }

  static async open(serverPort: number, host = '127.0.0.1'): Promise<OscClient> {
    const socket = createSocket('udp4');
    await new Promise<void>(resolve => socket.bind(0, host, () => resolve()));
    return new OscClient(socket, socket.address().port, host, serverPort);
  }

  send(address: string, args: readonly OscValue[] = []): Promise<void> {
    return new Promise((resolve, reject) =>
      this.socket.send(encodeOsc(address, args), this.serverPort, this.host, error =>
        error ? reject(error) : resolve()
      )
    );
  }

  /** `/anode/subscribe port`: feedback comes to this client's port. */
  subscribe(): Promise<void> {
    return this.send('/anode/subscribe', [{ int: this.port }]);
  }

  close(): void {
    this.socket.close();
  }
}
