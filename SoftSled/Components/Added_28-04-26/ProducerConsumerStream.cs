using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace SoftSled.Components {

    public class ProducerConsumerStream : Stream {
        private readonly Queue<byte[]> _chunkQueue = new Queue<byte[]>();
        private int _offsetInFirstChunk = 0;
        private bool _isCompleted = false;
        private readonly object _lock = new object();

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;

        // Length and Position are not supported in a continuous network pipe
        public override long Length => throw new NotSupportedException();
        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>
        /// Writes data to the stream. This is called by your AsfFrameAssembler.
        /// </summary>
        public override void Write(byte[] buffer, int offset, int count) {
            if (count <= 0) return;

            lock (_lock) {
                if (_isCompleted)
                    throw new InvalidOperationException("Cannot write to a completed stream.");

                // Create a copy of the incoming data to enqueue. 
                // This ensures the caller can safely reuse their buffer.
                byte[] chunk = new byte[count];
                Buffer.BlockCopy(buffer, offset, chunk, 0, count);

                _chunkQueue.Enqueue(chunk);

                // Wake up any threads waiting in the Read method
                Monitor.Pulse(_lock);
            }
        }

        /// <summary>
        /// Reads data from the stream. This is called by libVLC.
        /// Blocks until data is available or the stream is marked as complete.
        /// </summary>
        public override int Read(byte[] buffer, int offset, int count) {
            if (count <= 0) return 0;

            lock (_lock) {
                // Block until data is available or writing is completed
                while (_chunkQueue.Count == 0 && !_isCompleted) {
                    Monitor.Wait(_lock);
                }

                // If the queue is empty and we are completed, return 0 to signal End Of File (EOF)
                if (_chunkQueue.Count == 0 && _isCompleted) {
                    return 0;
                }

                byte[] firstChunk = _chunkQueue.Peek();
                int availableBytesInChunk = firstChunk.Length - _offsetInFirstChunk;
                int bytesToCopy = Math.Min(count, availableBytesInChunk);

                Buffer.BlockCopy(firstChunk, _offsetInFirstChunk, buffer, offset, bytesToCopy);

                _offsetInFirstChunk += bytesToCopy;

                // If we've consumed the entire chunk, remove it from the queue
                if (_offsetInFirstChunk >= firstChunk.Length) {
                    _chunkQueue.Dequeue();
                    _offsetInFirstChunk = 0;
                }

                return bytesToCopy;
            }
        }

        /// <summary>
        /// Signals that no more data will be written. 
        /// Allows the Read method to eventually return 0 (EOF) so the player can gracefully stop.
        /// </summary>
        public void CompleteWriting() {
            lock (_lock) {
                _isCompleted = true;
                Monitor.PulseAll(_lock); // Wake up any pending reads
            }
        }

        public override void Flush() {
            // Not required for this implementation since writes are immediately queued.
        }

        public override long Seek(long offset, SeekOrigin origin) {
            throw new NotSupportedException("Seeking is not supported on a ProducerConsumerStream.");
        }

        public override void SetLength(long value) {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing) {
            if (disposing) {
                CompleteWriting();
            }
            base.Dispose(disposing);
        }
    }
}
