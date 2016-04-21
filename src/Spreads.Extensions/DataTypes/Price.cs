﻿/*
    Copyright(c) 2014-2016 Victor Baybekov.

    This file is a part of Spreads library.

    Spreads library is free software; you can redistribute it and/or modify it under
    the terms of the GNU General Public License as published by
    the Free Software Foundation; either version 3 of the License, or
    (at your option) any later version.

    Spreads library is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.If not, see<http://www.gnu.org/licenses/>.
*/

using System;
using System.Runtime.InteropServices;

namespace Spreads.DataTypes {

    /// <summary>
    /// A blittable structure to store positive price values with decimal precision up to 15 digits.
    /// </summary>
    /// <remarks>
    ///  0                   1                   2                   3
    ///  0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
    /// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
    /// |R R R R|  -exp |        Int56 mantissa                         |
    /// +-------------------------------+-+-+---------------------------+
    /// |               Int56 mantissa                                  |
    /// +-------------------------------+-+-+---------------------------+
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct Price : IComparable<Price>, IEquatable<Price> {
        private const ulong MantissaMask = ((1L << 56) - 1L);
        private readonly ulong _value;

        public ulong Exponent => (_value >> 56); // needs & 15UL when the first 4 bits are used
        public ulong Mantissa => _value & MantissaMask;
        public decimal AsDecimal => (this);
        public double AsDouble => (this);

        private static decimal[] DecimalFractions10 = new decimal[] {
            1M,
            0.1M,
            0.01M,
            0.001M,
            0.0001M,
            0.00001M,
            0.000001M,
            0.0000001M,
            0.00000001M,
            0.000000001M,
            0.0000000001M,
            0.00000000001M,
            0.000000000001M,
            0.0000000000001M,
            0.00000000000001M,
            0.000000000000001M,
        };


        private static double[] DoubleFractions10 = new double[] {
            1,
            0.1,
            0.01,
            0.001,
            0.0001,
            0.00001,
            0.000001,
            0.0000001,
            0.00000001,
            0.000000001,
            0.0000000001,
            0.00000000001,
            0.000000000001,
            0.0000000000001,
            0.00000000000001,
            0.000000000000001,
        };

        private static ulong[] Powers10 = new ulong[] {
            1,
            10,
            100,
            1000,
            10000,
            100000,
            1000000,
            10000000,
            100000000,
            1000000000,
            10000000000,
            100000000000,
            1000000000000,
            10000000000000,
            100000000000000,
            1000000000000000,
        };

        public Price(int exponent, long mantissa) {
            if ((ulong)exponent > 15) throw new ArgumentOutOfRangeException(nameof(exponent));
            if ((ulong)mantissa > MantissaMask) throw new ArgumentOutOfRangeException(nameof(mantissa));
            _value = ((ulong)exponent << 56) | ((ulong)mantissa);
        }

        public Price(decimal value, int precision = 5) {
            if ((ulong)precision > 15) throw new ArgumentOutOfRangeException(nameof(precision));
            if (value > MantissaMask * DecimalFractions10[precision]) throw new ArgumentOutOfRangeException(nameof(value));
            var mantissa = decimal.ToUInt64(value * Powers10[precision]);
            _value = ((ulong)precision << 56) | ((ulong)mantissa);
        }

        public Price(double value, int precision = 5) {
            if ((ulong)precision > 15) throw new ArgumentOutOfRangeException(nameof(precision));
            if (value > MantissaMask * DoubleFractions10[precision]) throw new ArgumentOutOfRangeException(nameof(value));
            var mantissa = (ulong)(value * Powers10[precision]);
            _value = ((ulong)precision << 56) | ((ulong)mantissa);
        }

        public Price(int value) {
            _value = (ulong)value;
        }

        public static implicit operator double(Price price) {
            return price.Mantissa * DoubleFractions10[price.Exponent];
        }

        public static implicit operator float(Price price) {
            return (float)(price.Mantissa * DoubleFractions10[price.Exponent]);
        }

        public static implicit operator decimal(Price price) {
            return price.Mantissa * DecimalFractions10[price.Exponent];
        }

        public int CompareTo(Price other) {
            var c = (int)this.Exponent - (int)other.Exponent;
            if (c == 0) {
                return this.Mantissa.CompareTo(other.Mantissa);
            }
            if (c > 0) {
                return (this.Mantissa * Powers10[c]).CompareTo(other.Mantissa);
            } else {
                return this.Mantissa.CompareTo(other.Mantissa * Powers10[-c]);
            }
        }

        public bool Equals(Price other) {
            return this.CompareTo(other) == 0;
        }

        public static bool operator ==(Price x, Price y) {
            return x.Equals(y);
        }
        public static bool operator !=(Price x, Price y) {
            return !x.Equals(y);
        }
    }
}