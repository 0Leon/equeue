﻿using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using ECommon.Components;
using ECommon.Logging;
using ECommon.Utilities;

namespace EQueue.Broker.Storage
{
    public unsafe partial class TFChunk : IDisposable
    {
        public const int WriteBufferSize = 8192;
        public const int ReadBufferSize = 8192;

        #region Private Variables

        private static readonly ILogger _logger = ObjectContainer.Resolve<ILoggerFactory>().Create(typeof(TFChunk));
        private static readonly ILogRecordParserProvider _logRecordParserProvider = ObjectContainer.Resolve<ILogRecordParserProvider>();

        private ChunkHeader _chunkHeader;
        private ChunkFooter _chunkFooter;

        private readonly string _filename;
        private readonly TFChunkManagerConfig _chunkConfig;
        private readonly ConcurrentQueue<ReaderWorkItem> _readerWorkItemQueue = new ConcurrentQueue<ReaderWorkItem>();

        private int _dataPosition;
        private volatile bool _isCompleted;

        private WriterWorkItem _writerWorkItem;

        private readonly ManualResetEventSlim _destroyEvent = new ManualResetEventSlim(false);

        #endregion

        #region Public Properties

        public string FileName { get { return _filename; } }
        public ChunkHeader ChunkHeader { get { return _chunkHeader; } }
        public ChunkFooter ChunkFooter { get { return _chunkFooter; } }
        public bool IsCompleted { get { return _isCompleted; } }
        public int DataPosition { get { return _dataPosition; } }
        public long GlobalDataPosition
        {
            get
            {
                return ChunkHeader.ChunkDataStartPosition + DataPosition;
            }
        }

        #endregion

        #region Constructors

        private TFChunk(string filename, TFChunkManagerConfig chunkConfig)
        {
            Ensure.NotNullOrEmpty(filename, "filename");
            Ensure.NotNull(chunkConfig, "chunkConfig");

            _filename = filename;
            _chunkConfig = chunkConfig;
        }

        #endregion

        #region Factory Methods

        public static TFChunk CreateNew(string filename, int chunkNumber, TFChunkManagerConfig config)
        {
            var chunk = new TFChunk(filename, config);

            try
            {
                chunk.InitNew(chunkNumber);
            }
            catch (Exception ex)
            {
                _logger.Error(string.Format("Chunk {0} create failed.", chunk), ex);
                chunk.Dispose();
                throw;
            }

            return chunk;
        }
        public static TFChunk FromCompletedFile(string filename, TFChunkManagerConfig config)
        {
            var chunk = new TFChunk(filename, config);

            try
            {
                chunk.InitCompleted();
            }
            catch (Exception ex)
            {
                _logger.Error(string.Format("Chunk {0} init from completed file failed.", chunk), ex);
                chunk.Dispose();
                throw;
            }

            return chunk;
        }
        public static TFChunk FromOngoingFile(string filename, TFChunkManagerConfig config)
        {
            var chunk = new TFChunk(filename, config);

            try
            {
                chunk.InitOngoing();
            }
            catch (Exception ex)
            {
                _logger.Error(string.Format("Chunk {0} init from ongoing file failed.", chunk), ex);
                chunk.Dispose();
                throw;
            }

            return chunk;
        }

        #endregion

        #region Init Methods

        private void InitCompleted()
        {
            //先判断文件是否存在
            var fileInfo = new FileInfo(_filename);
            if (!fileInfo.Exists)
            {
                throw new CorruptDatabaseException(new ChunkNotFoundException(_filename));
            }

            //标记当前Chunk为已完成
            _isCompleted = true;

            //读取文件的头尾，检查文件是否有效
            using (var fileStream = new FileStream(_filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, ReadBufferSize, FileOptions.RandomAccess))
            {
                using (var reader = new BinaryReader(fileStream))
                {
                    //读取ChunkHeader和ChunkFooter
                    _chunkHeader = ReadHeader(fileStream, reader);
                    _chunkFooter = ReadFooter(fileStream, reader);

                    //检查Chunk文件的实际大小是否正确
                    var chunkFileSize = ChunkHeader.Size + _chunkFooter.ChunkDataTotalSize + ChunkFooter.Size;
                    if (chunkFileSize != fileStream.Length)
                    {
                        throw new CorruptDatabaseException(new BadChunkInDatabaseException(
                            string.Format("The size of chunk {0} should be equals with fileStream's length {1}, but instead it was {2}.",
                                            this,
                                            fileStream.Length,
                                            chunkFileSize)));
                    }

                    //如果Chunk中的数据是固定大小的，则还需要检查数据总数是否正确
                    if (IsFixedDataSize())
                    {
                        if (_chunkFooter.ChunkDataTotalSize != _chunkHeader.ChunkDataTotalSize)
                        {
                            throw new CorruptDatabaseException(new BadChunkInDatabaseException(
                                string.Format("The total data size of chunk {0} should be {1}, but instead it was {2}.",
                                                this,
                                                _chunkHeader.ChunkDataTotalSize,
                                                _chunkFooter.ChunkDataTotalSize)));
                        }
                    }
                }
            }

            _dataPosition = _chunkFooter.ChunkDataTotalSize;

            //初始化文件属性以及创建读文件的Reader
            SetAttributes();
            InitializeReaderWorkItems();
        }
        private void InitNew(int chunkNumber)
        {
            //初始化ChunkHeader
            var chunkDataSize = 0;
            if (_chunkConfig.ChunkDataSize > 0)
            {
                chunkDataSize = _chunkConfig.ChunkDataSize;
            }
            else
            {
                chunkDataSize =(_chunkConfig.ChunkDataUnitSize + 2 * sizeof(int)) * _chunkConfig.ChunkDataCount;
            }

            _chunkHeader = new ChunkHeader(chunkNumber, chunkDataSize);

            //计算Chunk文件大小
            var fileSize = ChunkHeader.Size + _chunkHeader.ChunkDataTotalSize + ChunkFooter.Size;

            //标记当前Chunk为未完成
            _isCompleted = false;

            //先创建临时文件，并将Chunk Header写入临时文件
            var tempFilename = string.Format("{0}.{1}.tmp", _filename, Guid.NewGuid());
            var tempFileStream = new FileStream(tempFilename, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, WriteBufferSize, FileOptions.SequentialScan);
            tempFileStream.SetLength(fileSize);
            tempFileStream.Write(_chunkHeader.AsByteArray(), 0, ChunkHeader.Size);
            tempFileStream.FlushToDisk();
            tempFileStream.Close();

            //将临时文件移动到正式的位置
            File.Move(tempFilename, _filename);

            //创建写文件的Writer
            var fileStream = new FileStream(_filename, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, WriteBufferSize, FileOptions.SequentialScan);
            fileStream.Position = ChunkHeader.Size;
            _dataPosition = 0;
            _writerWorkItem = new WriterWorkItem(fileStream);

            //初始化文件属性以及创建读文件的Reader
            SetAttributes();
            InitializeReaderWorkItems();
        }
        private void InitOngoing()
        {
            //先判断文件是否存在
            var fileInfo = new FileInfo(_filename);
            if (!fileInfo.Exists)
            {
                throw new CorruptDatabaseException(new ChunkNotFoundException(_filename));
            }

            //标记当前Chunk为未完成
            _isCompleted = false;

            //读取ChunkHeader和并设置下一个数据的文件流写入起始位置
            using (var fileStream = new FileStream(_filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, ReadBufferSize, FileOptions.SequentialScan))
            {
                using (var reader = new BinaryReader(fileStream))
                {
                    _chunkHeader = ReadHeader(fileStream, reader);
                    SetStreamWriteStartPosition(fileStream, reader);
                    _dataPosition = (int)fileStream.Position - ChunkHeader.Size;
                }
            }

            //创建写文件的Writer
            var writeFileStream = new FileStream(_filename, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, WriteBufferSize, FileOptions.SequentialScan);
            writeFileStream.Position = _dataPosition + ChunkHeader.Size;
            _writerWorkItem = new WriterWorkItem(writeFileStream);

            //初始化文件属性以及创建读文件的Reader
            SetAttributes();
            InitializeReaderWorkItems();
        }

        #endregion

        #region Read Methods

        public RecordReadResult TryReadAt(long dataPosition)
        {
            var readerWorkItem = GetReaderWorkItem();
            try
            {
                if (dataPosition >= DataPosition)
                {
                    return RecordReadResult.Failure;
                }

                ILogRecord record;
                var result = TryReadForwardInternal(readerWorkItem, dataPosition, out record);
                return new RecordReadResult(result, record);
            }
            finally
            {
                ReturnReaderWorkItem(readerWorkItem);
            }
        }

        private bool TryReadForwardInternal(ReaderWorkItem workItem, long dataPosition, out ILogRecord record)
        {
            record = null;

            if (dataPosition + 2 * sizeof(int) > DataPosition) // no space even for length prefix and suffix
            {
                return false;
            }

            workItem.Stream.Position = GetStreamPosition(dataPosition);

            var length = workItem.Reader.ReadInt32();
            if (length <= 0)
            {
                throw new InvalidReadException(
                    string.Format("Log record at data position {0} has non-positive length: {1} in chunk {2}",
                                  dataPosition, length, this));
            }
            if (length > _chunkConfig.MaxLogRecordSize)
            {
                throw new InvalidReadException(
                    string.Format("Log record at data position {0} has too large length: {1} bytes, while limit is {2} bytes, in chunk {3}",
                                  dataPosition, length, _chunkConfig.MaxLogRecordSize, this));
            }
            if (dataPosition + length + 2 * sizeof(int) > DataPosition)
            {
                throw new InvalidReadException(
                    string.Format("There is not enough space to read full record (length prefix: {0}). Actual pre-position: {1}. Something is seriously wrong in chunk {2}.",
                                  length, dataPosition, this));
            }

            var recordType = workItem.Reader.ReadByte();
            var recordParser = _logRecordParserProvider.GetLogRecordParser(recordType);
            record = recordParser.ParseFrom(workItem.Reader);

            // verify suffix length == prefix length
            int suffixLength = workItem.Reader.ReadInt32();
            if (suffixLength != length)
            {
                throw new Exception(
                    string.Format("Prefix/suffix length inconsistency: prefix length({0}) != suffix length ({1}). Actual pre-position: {2}. Something is seriously wrong in chunk {3}.",
                                  length, suffixLength, dataPosition, this));
            }

            return true;
        }

        #endregion

        #region Append Methods

        public RecordWriteResult TryAppend(ILogRecord record)
        {
            if (_isCompleted)
            {
                throw new ChunkWriteException(this.ToString(), "Cannot write to a read-only chunk.");
            }

            var writerWorkItem = _writerWorkItem;
            var bufferStream = writerWorkItem.BufferStream;
            var bufferStreamWriter = writerWorkItem.BufferWriter;

            bufferStream.SetLength(4);
            bufferStream.Position = 4;
            bufferStreamWriter.Write(record.Type);
            record.WriteTo(bufferStreamWriter);
            var recordLength = (int)bufferStream.Length - 4;
            bufferStreamWriter.Write(recordLength); // write record length suffix
            bufferStream.Position = 0;
            bufferStreamWriter.Write(recordLength); // write record length prefix

            if (recordLength > _chunkConfig.MaxLogRecordSize)
            {
                throw new ChunkWriteException(this.ToString(),
                    string.Format("Log record at data position {0} has too large length: {1} bytes, while limit is {2} bytes",
                                  _dataPosition, recordLength, _chunkConfig.MaxLogRecordSize));
            }

            if (IsFixedDataSize() && recordLength != _chunkConfig.ChunkDataUnitSize)
            {
                throw new ChunkWriteException(this.ToString(), string.Format("Invalid fixed data length, expected length {0}, but was {1}", _chunkConfig.ChunkDataUnitSize, recordLength));
            }

            if (writerWorkItem.FileStream.Position + recordLength + 2 * sizeof(int) > ChunkHeader.Size + _chunkHeader.ChunkDataTotalSize)
            {
                return RecordWriteResult.NotEnoughSpace();
            }

            var writtenPosition = _dataPosition;
            var buffer = bufferStream.GetBuffer();
            writerWorkItem.AppendData(buffer, 0, (int)bufferStream.Length);

            _dataPosition = (int)writerWorkItem.FileStream.Position - ChunkHeader.Size;

            return RecordWriteResult.Successful(ChunkHeader.ChunkDataStartPosition + writtenPosition);
        }

        #endregion

        #region Complete Methods

        public void Flush()
        {
            if (_isCompleted)
            {
                throw new InvalidOperationException(string.Format("Cannot flush a read-only TFChunk: {0}", this));
            }

            _writerWorkItem.FlushToDisk();
        }
        public void Complete()
        {
            if (_isCompleted)
            {
                throw new ChunkCompleteException(string.Format("Cannot complete a read-only TFChunk: {0}", this));
            }

            _chunkFooter = WriteFooter();
            Flush();
            _isCompleted = true;

            _writerWorkItem.Dispose();
            _writerWorkItem = null;
            SetAttributes();
        }

        #endregion

        #region Clean Methods

        public void Dispose()
        {
            Close();
        }
        public void Close()
        {
            CloseAllReaderWorkItems();
            if (!_isCompleted)
            {
                Flush();
            }
        }
        public void MarkForDeletion()
        {
            //_selfdestructin54321 = true;
            //_deleteFile = true;
            TryDestructFileStreams();
        }
        public void WaitForDestroy(int timeoutMs)
        {
            if (!_destroyEvent.Wait(timeoutMs))
            {
                throw new TimeoutException();
            }
        }

        private void TryDestructFileStreams()
        {
            //int fileStreamCount = int.MaxValue;

            //ReaderWorkItem workItem;
            //while (_readerWorkItemQueue.TryDequeue(out workItem))
            //{
            //    workItem.Stream.Dispose();
            //    fileStreamCount = Interlocked.Decrement(ref _fileStreamCount);
            //}

            //if (fileStreamCount < 0)
            //{
            //    throw new Exception("Somehow we managed to decrease count of file streams below zero.");
            //}
            //if (fileStreamCount == 0) // we are the last who should "turn the light off" for file streams
            //{
            //    CleanFileStreamDestruction();
            //}
        }
        private void CleanFileStreamDestruction()
        {
            if (_writerWorkItem != null)
            {
                _writerWorkItem.Dispose();
            }

            Helper.EatException(() => File.SetAttributes(_filename, FileAttributes.Normal));

            //if (_deleteFile)
            //{
            //    _logger.InfoFormat("File {0} has been marked for delete and will be deleted in TryDestructFileStreams.", Path.GetFileName(_filename));
            //    Helper.EatException(() => File.Delete(_filename));
            //}

            _destroyEvent.Set();
        }

        #endregion

        #region Helper Methods

        private T ReadChunk<T>(Func<ReaderWorkItem, T> readFunc)
        {
            var reader = GetReaderWorkItem();
            try
            {
                return readFunc(reader);
            }
            finally
            {
                ReturnReaderWorkItem(reader);
            }
        }
        private void ReadChunk(Action<ReaderWorkItem> readAction)
        {
            var reader = GetReaderWorkItem();
            try
            {
                readAction(reader);
            }
            finally
            {
                ReturnReaderWorkItem(reader);
            }
        }
        private void InitializeReaderWorkItems()
        {
            for (var i = 0; i < _chunkConfig.ChunkReaderCount; i++)
            {
                _readerWorkItemQueue.Enqueue(CreateReaderWorkItem());
            }
        }
        private void CloseAllReaderWorkItems()
        {
            ReaderWorkItem readerWorkItem;
            while (_readerWorkItemQueue.TryDequeue(out readerWorkItem))
            {
                readerWorkItem.Reader.Close();
            }
        }
        private ReaderWorkItem CreateReaderWorkItem()
        {
            var fileStream = new FileStream(_filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, ReadBufferSize, FileOptions.RandomAccess);
            var reader = new BinaryReader(fileStream);
            return new ReaderWorkItem(fileStream, reader);
        }
        private ReaderWorkItem GetReaderWorkItem()
        {
            ReaderWorkItem readerWorkItem;
            while (!_readerWorkItemQueue.TryDequeue(out readerWorkItem))
            {
                Thread.Sleep(1);
            }
            return readerWorkItem;
        }
        private void ReturnReaderWorkItem(ReaderWorkItem readerWorkItem)
        {
            _readerWorkItemQueue.Enqueue(readerWorkItem);
        }

        private ChunkFooter WriteFooter()
        {
            var currentTotalDataSize = DataPosition;

            //如果是固定大小的数据，则检查总数据大小是否正确
            if (IsFixedDataSize())
            {
                if (currentTotalDataSize != _chunkHeader.ChunkDataTotalSize)
                {
                    throw new ChunkCompleteException(string.Format("Cannot write the chunk footer as the current total data size is incorrect. chunk: {0}, expectTotalDataSize: {1}, currentTotalDataSize: {2}",
                        this,
                        _chunkHeader.ChunkDataTotalSize,
                        currentTotalDataSize));
                }
            }

            var workItem = _writerWorkItem;
            var footer = new ChunkFooter(currentTotalDataSize);

            workItem.AppendData(footer.AsByteArray(), 0, ChunkFooter.Size);

            Flush(); // trying to prevent bug with resized file, but no data in it

            var oldStreamLength = workItem.FileStream.Length;
            var newStreamLength = ChunkHeader.Size + currentTotalDataSize + ChunkFooter.Size;

            if (newStreamLength != oldStreamLength)
            {
                workItem.ResizeStream(newStreamLength);
            }

            return footer;
        }
        private ChunkHeader ReadHeader(FileStream stream, BinaryReader reader)
        {
            if (stream.Length < ChunkHeader.Size)
            {
                throw new Exception(string.Format("Chunk file '{0}' is too short to even read ChunkHeader, its size is {1} bytes.", _filename, stream.Length));
            }
            stream.Seek(0, SeekOrigin.Begin);
            return ChunkHeader.FromStream(reader, stream);
        }
        private ChunkFooter ReadFooter(FileStream stream, BinaryReader reader)
        {
            if (stream.Length < ChunkFooter.Size)
            {
                throw new Exception(string.Format("Chunk file '{0}' is too short to even read ChunkFooter, its size is {1} bytes.", _filename, stream.Length));
            }
            stream.Seek(-ChunkFooter.Size, SeekOrigin.End);
            return ChunkFooter.FromStream(reader, stream);
        }

        private bool IsFixedDataSize()
        {
            return _chunkConfig.ChunkDataSize <= 0;
        }
        private void SetStreamWriteStartPosition(FileStream stream, BinaryReader reader)
        {
            stream.Position = ChunkHeader.Size;

            var startStreamPosition = stream.Position;
            var maxStreamPosition = stream.Length - ChunkFooter.Size;
            var isFixedDataSize = IsFixedDataSize();

            while (stream.Position < maxStreamPosition)
            {
                if (TryReadRecord(stream, reader, isFixedDataSize, maxStreamPosition))
                {
                    startStreamPosition = stream.Position;
                }
                else
                {
                    break;
                }
            }

            if (startStreamPosition != stream.Position)
            {
                stream.Position = startStreamPosition;
            }
        }
        private bool TryReadRecord(FileStream stream, BinaryReader reader, bool isFixedDataSize, long maxStreamPosition)
        {
            try
            {
                var startStreamPosition = stream.Position;

                if (startStreamPosition + 2 * sizeof(int) > maxStreamPosition)
                {
                    return false;
                }
                var length = reader.ReadInt32();
                if (length <= 0 || length > _chunkConfig.MaxLogRecordSize)
                {
                    return false;
                }
                if (isFixedDataSize && length != _chunkConfig.ChunkDataUnitSize)
                {
                    _logger.ErrorFormat("Invalid fixed data length, expected length {0}, but was {1}", _chunkConfig.ChunkDataUnitSize, length);
                    return false;
                }
                if (startStreamPosition + length + 2 * sizeof(int) > maxStreamPosition)
                {
                    return false;
                }

                var recordType = reader.ReadByte();
                var recordParser = _logRecordParserProvider.GetLogRecordParser(recordType);
                var record = recordParser.ParseFrom(reader);
                if (record == null)
                {
                    return false;
                }

                int suffixLength = reader.ReadInt32();
                if (suffixLength != length)
                {
                    return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
        private static long GetStreamPosition(long dataPosition)
        {
            return ChunkHeader.Size + dataPosition;
        }

        private void SetAttributes()
        {
            Helper.EatException(() =>
            {
                if (_isCompleted)
                {
                    File.SetAttributes(_filename, FileAttributes.ReadOnly | FileAttributes.NotContentIndexed);
                }
                else
                {
                    File.SetAttributes(_filename, FileAttributes.NotContentIndexed);
                }
            });
        }

        #endregion

        public override string ToString()
        {
            return string.Format("#{0} ({1})", _chunkHeader.ChunkNumber, _filename);
        }
    }
}
