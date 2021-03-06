﻿using Spreads.Buffers;
using Spreads.Serialization;
using System;
using System.Buffers;
using System.Linq;
using System.Runtime.InteropServices;

namespace Spreads.Core.Run
{
    class Program
    {
        static void Main(string[] args)
        {
            LZ4();
            Zstd();
            Console.ReadLine();
        }

        static unsafe void LZ4()
        {
            var rng = new Random();

            var dest = (OwnedBuffer<byte>)new byte[1000000];
            var buffer = dest.Buffer;
            var handle = buffer.Pin();
            var ptr = (IntPtr)handle.PinnedPointer;

            var source = new decimal[10000];
            for (var i = 0; i < 10000; i++)
            {
                source[i] = i;
            }

            var len = BinarySerializer.Write(source, ref buffer, 0, null,
                CompressionMethod.LZ4);

            Console.WriteLine($"Useful: {source.Length * 16}");
            Console.WriteLine($"Total: {len}");

            var destination = new decimal[10000];

            var len2 = BinarySerializer.Read(buffer, out destination);

            if (source.SequenceEqual(destination))
            {
                Console.WriteLine("LZ4 OK");
            }
            handle.Free();

        }

        static unsafe void Zstd()
        {
            var rng = new Random();

            var dest = (OwnedBuffer<byte>)new byte[1000000];
            var buffer = dest.Buffer;
            var handle = buffer.Pin();
            var ptr = (IntPtr)handle.PinnedPointer;

            var source = new decimal[10000];
            for (var i = 0; i < 10000; i++)
            {
                source[i] = i;
            }

            var len = BinarySerializer.Write(source, ref buffer, 0, null,
                CompressionMethod.Zstd);

            Console.WriteLine($"Useful: {source.Length * 16}");
            Console.WriteLine($"Total: {len}");

            var destination = new decimal[10000];

            var len2 = BinarySerializer.Read(buffer, out destination);

            if (source.SequenceEqual(destination))
            {
                Console.WriteLine("Zstd OK");
            }
            handle.Free();
        }
    }
}