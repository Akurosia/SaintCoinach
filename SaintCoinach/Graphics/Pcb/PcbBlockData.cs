using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SaintCoinach.Graphics.Pcb {
    public class PcbBlockData {
        public struct VertexI16 {
            public ushort X;
            public ushort Y;
            public ushort Z;
        };
        public struct IndexData {
            public byte Index1;
            public byte Index2;
            public byte Index3;
            public byte Unknown1;
            public byte Unknown2;
            public byte Unknown3;
            public byte Unknown4;
            public byte Unknown5;
            public byte Unknown6;
            public byte Unknown7;
            public byte Unknown8;
            public byte Unknown9;
        };

        public Vector3[] Vertices;
        public VertexI16[] VerticesI16;
        public IndexData[] Indices;
    }
}
