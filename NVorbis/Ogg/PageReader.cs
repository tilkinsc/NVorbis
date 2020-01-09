﻿using NVorbis.Contracts;
using NVorbis.Contracts.Ogg;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace NVorbis.Ogg
{
    internal class PageReader : IPageReader
    {
        internal static Func<ICrc> CreateCrc { get; set; } = () => new Crc();
        internal static Func<IPageReader, IPacketReader> CreatePacketProvider { get; set; } = pr => new PacketProvider(pr);

        private readonly Dictionary<int, IPacketReader> _packetProviders = new Dictionary<int, IPacketReader>();
        private readonly HashSet<int> _ignoredSerials = new HashSet<int>();
        private readonly ICrc _crc = CreateCrc();
        private readonly object _readLock = new object();
        private readonly byte[] _headerBuf = new byte[282];

        private Stream _stream;
        private bool _closeOnDispose;
        private readonly Func<IPacketProvider, bool> _newStreamCallback;
        private long _nextPageOffset;

        public PageReader(Stream stream, bool closeOnDispose, Func<IPacketProvider, bool> newStreamCallback)
        {
            _stream = stream;
            _closeOnDispose = closeOnDispose;
            _newStreamCallback = newStreamCallback;
        }

        public void Lock()
        {
            Monitor.Enter(_readLock);
        }

        bool CheckLock()
        {
            return Monitor.IsEntered(_readLock);
        }

        public bool Release()
        {
            if (Monitor.IsEntered(_readLock))
            {
                Monitor.Exit(_readLock);
                return true;
            }
            return false;
        }

        // global values
        public long ContainerBits { get; private set; }
        public long WasteBits { get; private set; }

        public long PageOffset { get; private set; }
        public int StreamSerial { get; private set; }
        public int SequenceNumber { get; private set; }
        public PageFlags PageFlags { get; private set; }
        public long GranulePosition { get; private set; }
        public short PacketCount { get; private set; }
        public bool? IsResync { get; private set; }
        public bool IsContinued { get; private set; }

        // look for the next page header, decode it, and check CRC
        public bool ReadNextPage()
        {
            // make sure we're locked; no sense reading if we aren't
            if (!CheckLock()) throw new InvalidOperationException("Must be locked prior to reading!");

            IsResync = false;
            _stream.Position = _nextPageOffset;
            var ofs = 0;
            int cnt;
            while ((cnt = _stream.Read(_headerBuf, ofs, _headerBuf.Length - ofs)) > 0)
            {
                cnt += ofs;
                for (var i = 0; i < cnt - 4; i++)
                {
                    // look for the capture sequence
                    if (_headerBuf[i] == 0x4f && _headerBuf[i + 1] == 0x67 && _headerBuf[i + 2] == 0x67 && _headerBuf[i + 3] == 0x53)
                    {
                        // cool, found it...

                        // move to the front of the buffer if not there already
                        if (i > 0)
                        {
                            Buffer.BlockCopy(_headerBuf, i, _headerBuf, 0, cnt);
                            WasteBits += i * 8;
                            IsResync = true;

                            // adjust our count and index to match what we just did
                            cnt -= i;
                            i = 0;
                        }

                        // note the file offset
                        var pageOffset = _stream.Position - cnt;

                        // try to make sure we have enough in the buffer
                        cnt += _stream.Read(_headerBuf, cnt, _headerBuf.Length - cnt);

                        // try to load the page
                        if (CheckPage(pageOffset, out var nextPageOffset))
                        {
                            // good packet!
                            _nextPageOffset = nextPageOffset;

                            // try to add it to the appropriate packet provider; if it returns false, we're ignoring the page's logical stream
                            if (!AddPage())
                            {
                                // we read a page, but it was for an ignored stream; try looking at the next page position
                                _stream.Position = nextPageOffset;

                                // the simplest way to do this is to jump to the outer loop and force a complete re-start of the process
                                // reset ofs so we're at the beginning of the buffer again
                                ofs = 0;
                                // reset cnt so the move logic at the bottom of the outer loop doesn't run
                                cnt = 0;

                                // update WasteBits since we just threw away an entire page
                                WasteBits += 8 * (nextPageOffset - pageOffset);

                                // bail out to the outer loop
                                break;
                            }
                            return true;
                        }

                        // meh, just reset the stream position to where it was before we tried that page
                        _stream.Position = pageOffset + cnt;
                    }
                }

                // no dice...  try again with a full buffer read
                if (cnt >= 3)
                {
                    _headerBuf[0] = _headerBuf[cnt - 3];
                    _headerBuf[1] = _headerBuf[cnt - 2];
                    _headerBuf[2] = _headerBuf[cnt - 1];
                    ofs = 3;
                    WasteBits += 8 * (cnt - 3);
                    IsResync = true;
                }
            }

            if (cnt == 0)
            {
                // we're EOF
                foreach (var pp in _packetProviders)
                {
                    pp.Value.SetEndOfStream();
                }
            }

            return false;
        }

        private bool CheckPage(long pageOffset, out long nextPageOffset)
        {
            if (DecodeHeader())
            {
                // we have a potentially good page... check the CRC
                var crc = BitConverter.ToUInt32(_headerBuf, 22);
                var segCount = _headerBuf[26];

                _crc.Reset();
                for (var j = 0; j < 22; j++)
                {
                    _crc.Update(_headerBuf[j]);
                }
                _crc.Update(0);
                _crc.Update(0);
                _crc.Update(0);
                _crc.Update(0);
                _crc.Update(segCount);

                // get the total size of the data while updating the CRC
                var dataLen = 0;
                for (var j = 0; j < segCount; j++)
                {
                    var segLen = _headerBuf[27 + j];
                    _crc.Update(segLen);
                    dataLen += segLen;
                }

                // finish calculating the CRC
                _stream.Position = pageOffset + 27 + segCount;
                var dataBuf = new byte[dataLen];
                if (_stream.Read(dataBuf, 0, dataLen) < dataLen)
                {
                    // we're going to assume this means the stream has ended
                    nextPageOffset = 0;
                    return false;
                }
                for (var j = 0; j < dataLen; j++)
                {
                    _crc.Update(dataBuf[j]);
                }

                if (_crc.Test(crc))
                {
                    // cool, we have a valid page!
                    nextPageOffset = _stream.Position;
                    PageOffset = pageOffset;
                    ContainerBits += 8 * (27 + segCount);
                    return true;
                }
            }
            nextPageOffset = 0;
            return false;
        }

        private bool AddPage()
        {
            if (!_packetProviders.ContainsKey(StreamSerial))
            {
                if (_ignoredSerials.Contains(StreamSerial))
                {
                    // nevermind... we're supposed to ignore these
                    return false;
                }

                var packetProvider = CreatePacketProvider(this);
                packetProvider.AddPage(PageFlags, IsResync.Value, SequenceNumber, PageOffset);
                _packetProviders.Add(StreamSerial, packetProvider);
                if (!_newStreamCallback(packetProvider))
                {
                    _packetProviders.Remove(StreamSerial);
                    _ignoredSerials.Add(StreamSerial);
                    packetProvider.Dispose();
                    return false;
                }
            }
            else
            {
                _packetProviders[StreamSerial].AddPage(PageFlags, IsResync.Value, SequenceNumber, PageOffset);
            }

            return true;
        }

        public bool ReadPageAt(long offset)
        {
            // make sure we're locked; no sense reading if we aren't
            if (!CheckLock()) throw new InvalidOperationException("Must be locked prior to reading!");

            // this should be safe; we've already checked the page by now

            if (offset == PageOffset)
            {
                // short circuit for when we've already loaded the page
                return true;
            }

            // we don't actually know anymore if the page is a resync; don't try to say one way or the other
            IsResync = null;

            _stream.Position = offset;
            _stream.Read(_headerBuf, 0, 27);
            _stream.Read(_headerBuf, 27, _headerBuf[26]);

            if (DecodeHeader())
            {
                PageOffset = offset;
                return true;
            }
            return false;
        }

        private bool DecodeHeader()
        {
            if (_headerBuf[0] == 0x4f && _headerBuf[1] == 0x67 && _headerBuf[2] == 0x67 && _headerBuf[3] == 0x53 && _headerBuf[4] == 0)
            {
                PageFlags = (PageFlags)_headerBuf[5];
                GranulePosition = BitConverter.ToInt64(_headerBuf, 6);
                StreamSerial = BitConverter.ToInt32(_headerBuf, 14);
                SequenceNumber = BitConverter.ToInt32(_headerBuf, 18);
                var pktLen = 0;
                short pktCnt = 0;
                for (var j = 0; j < _headerBuf[26]; j++)
                {
                    var segLen = _headerBuf[27 + j];
                    pktLen += segLen;
                    if (segLen < 255)
                    {
                        if (pktLen > 0)
                        {
                            ++pktCnt;
                            pktLen = 0;
                        }
                    }
                }
                // if the pktLen has a value left and the last segment isn't 255 (continued), count the packet
                if (pktLen > 0 && !(IsContinued = _headerBuf[26 + _headerBuf[26]] == 255))
                {
                    ++pktCnt;
                }
                PacketCount = pktCnt;
                return true;
            }
            return false;
        }

        public List<Tuple<long, int>> GetPackets()
        {
            if (!CheckLock()) throw new InvalidOperationException("Must be locked!");

            var segCnt = _headerBuf[26];
            var dataOffset = PageOffset + 27 + segCnt;
            var packets = new List<Tuple<long, int>>(segCnt);

            if (segCnt > 0)
            {
                var size = 0;
                for (int i = 0, idx = 27; i < segCnt; i++, idx++)
                {
                    size += _headerBuf[idx];
                    if (_headerBuf[idx] < 255)
                    {
                        if (size > 0)
                        {
                            packets.Add(new Tuple<long, int>(dataOffset, size));
                            dataOffset += size;
                        }
                        size = 0;
                    }
                }
                if (size > 0)
                {
                    packets.Add(new Tuple<long, int>(dataOffset, size));
                }
            }
            return packets;
        }

        public int Read(long offset, byte[] buffer, int index, int count)
        {
            lock (_readLock)
            {
                _stream.Position = offset;
                return _stream.Read(buffer, index, count);
            }
        }

        internal int GetStreamPagesRead(int streamSerial)
        {
            if (_packetProviders.TryGetValue(streamSerial, out var pp))
            {
                return pp.PagesRead;
            }
            return 0;
        }

        public void ReadAllPages()
        {
            if (!CheckLock()) throw new InvalidOperationException("Must be locked!");

            while (ReadNextPage()) { };
        }

        internal int GetStreamTotalPageCount(int streamSerial)
        {
            if (_packetProviders.TryGetValue(streamSerial, out var pp))
            {
                return pp.GetTotalPageCount();
            }
            return 0;
        }

        public void Dispose()
        {
            foreach (var pp in _packetProviders)
            {
                pp.Value.Dispose();
            }
            _packetProviders.Clear();

            if (_closeOnDispose)
            {
                _stream?.Dispose();
            }
            _stream = null;
        }
    }
}