using System;
using System.Text;
using Hazel;

namespace UsefulTORStuff {
    // Audit 16.11. TOR-002: erst begrenzt lesen, danach den unveränderten Hazel-Reader weitergeben.
    internal sealed class AuditFixA_Payload {
        private readonly byte[] bytes;
        private int position;
        internal int Remaining => bytes.Length - position;
        internal AuditFixA_Payload(MessageReader reader) {
            if (reader == null || reader.Buffer == null || reader.Offset < 0 || reader.Position < 0
                || reader.Length < reader.Position) throw new FormatException("Ungültiger Reader-Bereich");
            int length = reader.Length - reader.Position;
            int start = checked(reader.Offset + reader.Position);
            if (length < 0 || length > 8192 || start < 0 || start > reader.Buffer.Length - length)
                throw new FormatException("RPC-Länge außerhalb der Grenze");
            bytes = new byte[length];
            for (int i = 0; i < length; i++) bytes[i] = reader.Buffer[start + i];
        }
        internal byte Byte() {
            if (Remaining < 1) throw new FormatException("RPC abgeschnitten");
            return bytes[position++];
        }
        internal bool Bool() {
            byte value = Byte();
            if (value > 1) throw new FormatException("Ungültiger Wahrheitswert");
            return value != 0;
        }
        internal uint Packed() {
            uint value = 0;
            for (int shift = 0; shift < 35; shift += 7) {
                byte next = Byte();
                if (shift == 28 && next > 15) throw new FormatException("Packed-Überlauf");
                value |= (uint)(next & 127) << shift;
                if ((next & 128) == 0) return value;
            }
            throw new FormatException("Ungültige Packed-Zahl");
        }
        internal float Float() {
            if (Remaining < 4) throw new FormatException("Float abgeschnitten");
            float value = BitConverter.ToSingle(bytes, position);
            position += 4;
            if (!float.IsFinite(value)) throw new FormatException("Nicht endlicher Float");
            return value;
        }
        internal string String(int limit = 512) {
            uint length = Packed();
            if (length > limit || length > Remaining) throw new FormatException("Ungültige Stringlänge");
            string value = new UTF8Encoding(false, true).GetString(bytes, position, (int)length);
            position += (int)length;
            return value;
        }
        internal void Skip(int count) {
            if (count < 0 || count > Remaining) throw new FormatException("RPC abgeschnitten");
            position += count;
        }
        // Integration TOR-002: nur die belegte Int32-Altform, keine beliebigen Restdaten.
        internal void OptionalZeroPadding() {
            if (Remaining == 0) return;
            if (Remaining != 3 || Byte() != 0 || Byte() != 0 || Byte() != 0)
                throw new FormatException("Ungültige Int32-Auffüllbytes");
        }
        internal void End() {
            if (Remaining != 0) throw new FormatException("Unerwartete RPC-Restdaten");
        }
    }
}
