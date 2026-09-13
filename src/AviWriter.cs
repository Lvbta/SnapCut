using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SimpleShot
{
    /// <summary>
    /// Minimal MJPEG AVI muxer with an optional 16-bit PCM audio stream. Each video frame is a
    /// JPEG (GDI+ encoded); audio (when present) is interleaved as "01wb" chunks. No external
    /// codecs or libraries. Playable in Windows Media Player / Movies&amp;TV / VLC.
    /// Header sizes are patched on Close() once frame/sample counts are known.
    /// </summary>
    internal sealed class AviWriter : IDisposable
    {
        private struct Chunk { public bool Audio; public int Offset; public int Size; }

        private readonly FileStream _fs;
        private readonly BinaryWriter _w;
        private readonly int _width, _height, _fps;
        private readonly List<Chunk> _index = new List<Chunk>();

        private readonly bool _hasAudio;
        private readonly int _channels, _sampleRate, _blockAlign, _avgBytesPerSec;
        private long _audioBytes;

        private long _posRiffSize, _posTotalFrames, _posStreamLength, _posMoviSize, _posAudioLength;
        private long _moviStart;
        private volatile bool _closed;   // UI 线程关闭、采集线程写入，必须 volatile 保证可见性
        private int _videoFrames;

        public int FrameCount { get { return _videoFrames; } }

        /// <summary>当前文件写入位置（字节）。用于监控 AVI 2GB 索引上限。</summary>
        public long Position { get { return _fs.Position; } }

        public AviWriter(string path, int width, int height, int fps)
            : this(path, width, height, fps, 0, 0) { }

        /// <summary>Pass channels &gt; 0 and sampleRate &gt; 0 to add a 16-bit PCM audio stream.</summary>
        public AviWriter(string path, int width, int height, int fps, int channels, int sampleRate)
        {
            _width = width; _height = height; _fps = fps;
            _hasAudio = channels > 0 && sampleRate > 0;
            if (_hasAudio)
            {
                _channels = channels;
                _sampleRate = sampleRate;
                _blockAlign = channels * 2;                 // 16-bit
                _avgBytesPerSec = sampleRate * _blockAlign;
            }
            _fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
            _w = new BinaryWriter(_fs);
            WriteHeaders();
        }

        private void Fourcc(string s) { _w.Write(Encoding.ASCII.GetBytes(s)); }

        private void WriteHeaders()
        {
            Fourcc("RIFF");
            _posRiffSize = _fs.Position; _w.Write(0);        // riff size (patched)
            Fourcc("AVI ");

            // ---- LIST hdrl ----
            int videoStrl = 8 + 4 + 8 + 56 + 8 + 40;         // LIST(strl) + strl + strh + strf(BITMAPINFOHEADER)
            int audioStrl = _hasAudio ? (8 + 4 + 8 + 56 + 8 + 16) : 0; // + strf(WAVEFORMATEX 16)
            int hdrlPayload = 4 + 8 + 56 + videoStrl + audioStrl;      // hdrl + avih + streams
            Fourcc("LIST");
            _w.Write(hdrlPayload);
            Fourcc("hdrl");

            // avih - main header (56 bytes)
            Fourcc("avih"); _w.Write(56);
            _w.Write(1000000 / _fps);                        // dwMicroSecPerFrame
            _w.Write(_width * _height * 3 * _fps);           // dwMaxBytesPerSec (upper bound)
            _w.Write(0);                                     // dwPaddingGranularity
            _w.Write(0x10);                                  // dwFlags = AVIF_HASINDEX
            _posTotalFrames = _fs.Position; _w.Write(0);     // dwTotalFrames (patched)
            _w.Write(0);                                     // dwInitialFrames
            _w.Write(_hasAudio ? 2 : 1);                     // dwStreams
            _w.Write(_width * _height * 3);                  // dwSuggestedBufferSize
            _w.Write(_width); _w.Write(_height);
            _w.Write(0); _w.Write(0); _w.Write(0); _w.Write(0); // reserved

            // ---- LIST strl (video) ----
            Fourcc("LIST");
            _w.Write(4 + 8 + 56 + 8 + 40);
            Fourcc("strl");

            // strh - stream header (56 bytes)
            Fourcc("strh"); _w.Write(56);
            Fourcc("vids"); Fourcc("MJPG");
            _w.Write(0);                                     // dwFlags
            _w.Write((short)0); _w.Write((short)0);          // priority, language
            _w.Write(0);                                     // initial frames
            _w.Write(1);                                     // dwScale
            _w.Write(_fps);                                  // dwRate  -> fps = rate/scale
            _w.Write(0);                                     // dwStart
            _posStreamLength = _fs.Position; _w.Write(0);    // dwLength (patched)
            _w.Write(_width * _height * 3);                  // suggested buffer
            _w.Write(-1);                                    // quality (default)
            _w.Write(0);                                     // sample size
            _w.Write((short)0); _w.Write((short)0);          // rcFrame
            _w.Write((short)_width); _w.Write((short)_height);

            // strf - BITMAPINFOHEADER (40 bytes)
            Fourcc("strf"); _w.Write(40);
            _w.Write(40); _w.Write(_width); _w.Write(_height);
            _w.Write((short)1); _w.Write((short)24);
            Fourcc("MJPG");
            _w.Write(_width * _height * 3);
            _w.Write(0); _w.Write(0); _w.Write(0); _w.Write(0);

            if (_hasAudio)
            {
                // ---- LIST strl (audio) ----
                Fourcc("LIST");
                _w.Write(4 + 8 + 56 + 8 + 16);
                Fourcc("strl");

                // strh (56 bytes)
                Fourcc("strh"); _w.Write(56);
                Fourcc("auds"); _w.Write(0);                 // fccType, fccHandler
                _w.Write(0);                                 // dwFlags
                _w.Write((short)0); _w.Write((short)0);      // priority, language
                _w.Write(0);                                 // initial frames
                _w.Write(_blockAlign);                       // dwScale
                _w.Write(_avgBytesPerSec);                   // dwRate
                _w.Write(0);                                 // dwStart
                _posAudioLength = _fs.Position; _w.Write(0); // dwLength in blocks (patched)
                _w.Write(_avgBytesPerSec);                   // suggested buffer
                _w.Write(-1);                                // quality
                _w.Write(_blockAlign);                       // sample size
                _w.Write((short)0); _w.Write((short)0);
                _w.Write((short)0); _w.Write((short)0);      // rcFrame

                // strf - WAVEFORMATEX (16 bytes, PCM)
                Fourcc("strf"); _w.Write(16);
                _w.Write((short)1);                          // WAVE_FORMAT_PCM
                _w.Write((short)_channels);
                _w.Write(_sampleRate);
                _w.Write(_avgBytesPerSec);
                _w.Write((short)_blockAlign);
                _w.Write((short)16);                         // bits per sample
            }

            // ---- LIST movi ----
            Fourcc("LIST");
            _posMoviSize = _fs.Position; _w.Write(0);        // movi size (patched)
            _moviStart = _fs.Position;
            Fourcc("movi");
        }

        /// <summary>Appends one JPEG-encoded video frame.</summary>
        public void AddFrame(byte[] jpeg, int length)
        {
            if (_closed) return;
            int offset = (int)(_fs.Position - _moviStart);
            Fourcc("00dc");
            _w.Write(length);
            _w.Write(jpeg, 0, length);
            if ((length & 1) == 1) _w.Write((byte)0);
            _index.Add(new Chunk { Audio = false, Offset = offset, Size = length });
            _videoFrames++;
        }

        /// <summary>Appends a block of 16-bit PCM audio (ignored if no audio stream).</summary>
        public void AddAudio(byte[] pcm, int length)
        {
            if (_closed || !_hasAudio || length <= 0) return;
            int offset = (int)(_fs.Position - _moviStart);
            Fourcc("01wb");
            _w.Write(length);
            _w.Write(pcm, 0, length);
            if ((length & 1) == 1) _w.Write((byte)0);
            _index.Add(new Chunk { Audio = true, Offset = offset, Size = length });
            _audioBytes += length;
        }

        public void Close()
        {
            if (_closed) return;
            _closed = true;

            long moviEnd = _fs.Position;

            // idx1
            Fourcc("idx1");
            _w.Write(_index.Count * 16);
            foreach (var c in _index)
            {
                Fourcc(c.Audio ? "01wb" : "00dc");
                _w.Write(0x10);                              // AVIIF_KEYFRAME
                _w.Write(c.Offset);
                _w.Write(c.Size);
            }
            long fileEnd = _fs.Position;

            // patch placeholders
            _fs.Position = _posRiffSize; _w.Write((int)(fileEnd - 8));
            _fs.Position = _posTotalFrames; _w.Write(_videoFrames);
            _fs.Position = _posStreamLength; _w.Write(_videoFrames);
            _fs.Position = _posMoviSize; _w.Write((int)(moviEnd - _moviStart));
            if (_hasAudio)
            {
                _fs.Position = _posAudioLength;
                _w.Write((int)(_audioBytes / _blockAlign)); // dwLength in sample blocks
            }

            _w.Flush();
            _fs.Close();
        }

        public void Dispose() { Close(); }
    }
}
