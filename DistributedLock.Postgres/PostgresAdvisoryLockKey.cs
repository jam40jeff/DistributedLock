﻿using Medallion.Threading.Internal;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Medallion.Threading.Postgres
{
    public readonly struct PostgresAdvisoryLockKey : IEquatable<PostgresAdvisoryLockKey>
    {
        private readonly long _key;
        private readonly KeyEncoding _keyEncoding;

        public PostgresAdvisoryLockKey(long key)
        {
            this._key = key;
            this._keyEncoding = KeyEncoding.Int64;
        }

        public PostgresAdvisoryLockKey(int key1, int key2)
        {
            this._key = CombineKeys(key1, key2);
            this._keyEncoding = KeyEncoding.Int32Pair;
        }

        public PostgresAdvisoryLockKey(string name, bool allowHashing = false)
        {
            if (name == null) { throw new ArgumentNullException(nameof(name)); }

            if (TryEncodeAscii(name, out this._key))
            {
                this._keyEncoding = KeyEncoding.Ascii;
            }
            else if (TryEncodeHashString(name, out this._key, out var hasSeparator))
            {
                this._keyEncoding = hasSeparator ? KeyEncoding.Int32Pair : KeyEncoding.Int64;
            }
            else if (allowHashing)
            {
                this._key = HashString(name);
                this._keyEncoding = KeyEncoding.Int64;
            }
            else
            {
                throw new FormatException($"Name '{name}' could not be encoded as a {nameof(PostgresAdvisoryLockKey)}. Please specify {nameof(allowHashing)} or use one of the following formats:"
                    + $" or (1) a 0-{MaxAsciiLength} character string using only non-0 ASCII characters"
                    + $", (2) a {HashStringLength} character hex string, such as the result of Int64.MaxValue.ToString(\"x{HashStringLength}\")"
                    + $", or (3) a 2-part, {SeparatedHashStringLength} character string of the form XXXXXXXX{HashStringSeparator}XXXXXXXX, where the X's are {HashPartLength} hex strings"
                    + $" such as the result of Int32.MaxValue.ToString(\"x{HashPartLength}\")."
                    + " Note that each unique string provided for formats 1 and 2 will map to a unique hash value, with no collisions across formats. Format 3 strings use the same key space as 2.");
            }
        }

        internal bool HasSingleKey => this._keyEncoding == KeyEncoding.Int64;
        
        public long Key
        {
            get
            {
                Invariant.Require(this.HasSingleKey);
                return this._key;
            }
        }

        public (int key1, int key2) Keys
        {
            get
            {
                Invariant.Require(!this.HasSingleKey);
                return SplitKeys(this._key);
            }
        }

        public bool Equals(PostgresAdvisoryLockKey that) => this.ToTuple().Equals(that.ToTuple());

        public override bool Equals(object obj) => obj is PostgresAdvisoryLockKey that && this.Equals(that);

        public override int GetHashCode() => this.ToTuple().GetHashCode();

        public static bool operator ==(PostgresAdvisoryLockKey a, PostgresAdvisoryLockKey b) => a.Equals(b);
        public static bool operator !=(PostgresAdvisoryLockKey a, PostgresAdvisoryLockKey b) => !(a == b);

        private (long, bool) ToTuple() => (this._key, this.HasSingleKey);

        public override string ToString() => this._keyEncoding switch
        {
            KeyEncoding.Int64 => ToHashString(this._key),
            KeyEncoding.Int32Pair => ToHashString(SplitKeys(this._key)),
            KeyEncoding.Ascii => ToAsciiString(this._key),
            _ => throw new InvalidOperationException()
        };

        private static long CombineKeys(int key1, int key2) => unchecked(((long)key1 << (8 * sizeof(int))) | (uint)key2);
        private static (int key1, int key2) SplitKeys(long key) => ((int)(key >> (8 * sizeof(int))), unchecked((int)(key & uint.MaxValue)));

        #region ---- Ascii ----
        // The ASCII encoding works as follows:
        // Each ASCII char is 7 bits allowing for 9 chars = 63 bits in total.
        // In order to differentiate between different-length strings with leading '\0', 
        // we additionally fill the next bit after the string ends with 0. We then fill any
        // remaining bits with 1. Therefore the final 64 bit value is 0-9 7-bit characters followed
        // by 0, followed by N=63-(7*length) 1s

        private const int AsciiCharBits = 7;
        private const int MaxAsciiValue = (1 << AsciiCharBits) - 1;
        internal const int MaxAsciiLength = (8 * sizeof(long)) / AsciiCharBits;

        private static bool TryEncodeAscii(string name, out long key)
        {
            if (name.Length > MaxAsciiLength)
            {
                key = default;
                return false;
            }

            // load the chars into result
            var result = 0L;
            foreach (var @char in name)
            {
                if (@char > MaxAsciiValue)
                {
                    key = default;
                    return false;
                }

                result = (result << AsciiCharBits) | @char;
            }

            // add padding
            result <<= 1; // load zero
            for (var i = name.Length; i < MaxAsciiLength; ++i)
            {
                result = (result << AsciiCharBits) | MaxAsciiValue; // load 1s
            }

            key = result;
            return true;
        }

        private static string ToAsciiString(long key)
        {
            // use unsigned to avoid signed shifts
            var remainingKeyBits = unchecked((ulong)key);

            // unload padding 1s to determine length
            var length = MaxAsciiLength;
            while ((remainingKeyBits & MaxAsciiValue) == MaxAsciiValue)
            {
                --length;
                remainingKeyBits >>= AsciiCharBits;
            }
            Invariant.Require((remainingKeyBits & 1) == 0, "last padding bit should be zero");
            remainingKeyBits >>= 1; // unload padding 0

            var chars = new char[length];
            for (var i = length - 1; i >= 0; --i)
            {
                chars[i] = (char)(remainingKeyBits & MaxAsciiValue);
                remainingKeyBits >>= AsciiCharBits;
            }

            return new string(chars, startIndex: 0, length);
        }
        #endregion

        #region ---- Hashing ----
        private const char HashStringSeparator = ',';
        internal const int HashPartLength = 8, // 8-byte hex numbers
            HashStringLength = 16, // 2 hashes
            SeparatedHashStringLength = HashStringLength + 1; // separated by comma

        private static bool TryEncodeHashString(string name, out long key, out bool hasSeparator)
        { 
            if (name.Length == SeparatedHashStringLength && name[HashPartLength] == HashStringSeparator)
            {
                hasSeparator = true;
            }
            else
            {
                hasSeparator = false;

                if (name.Length != HashStringLength)
                {
                    key = default;
                    return false;
                }
            }

            return TryParseHashKeys(name, out key);

            static bool TryParseHashKeys(string text, out long key)
            {
                if (TryParseHashKey(text.Substring(0, HashPartLength), out var key1)
                    && TryParseHashKey(text.Substring(text.Length - HashPartLength), out var key2))
                {
                    key = CombineKeys(key1, key2);
                    return true;
                }

                key = default;
                return false;
            }

            static bool TryParseHashKey(string text, out int key) =>
                int.TryParse(text, NumberStyles.AllowHexSpecifier, NumberFormatInfo.InvariantInfo, out key);
        }

        public static long HashString(string name)
        {
            // The hash result from SHA1 is too large, so we have to truncate (recommended practice and does not
            // weaken the hash other than due to using fewer bytes)

            using (var sha1 = SHA1.Create())
            {
                var hashBytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(name));

                // We don't use BitConverter here because we want to be endianess-agnostic. 
                // However, this code replicates that result on little-endian
                var result = 0L;
                for (var i = sizeof(long) - 1; i >= 0; --i)
                {
                    result = (result << 8) | hashBytes[i];
                }
                return result;
            }
        }

        private static string ToHashString((int key1, int key2) keys) => FormattableString.Invariant($"{keys.key1:x8}{HashStringSeparator}{keys.key2:x8}");

        private static string ToHashString(long key) => key.ToString("x16", NumberFormatInfo.InvariantInfo);
        #endregion

        private enum KeyEncoding
        {
            Int64 = 0,
            Int32Pair,
            Ascii,
        }
    }
}
