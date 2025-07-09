//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

public class ProducerConsumerFileWriter : IDisposable
{
    private readonly FileStream _fs;
    private readonly BlockingCollection<byte[]> _queue;
    private readonly Task _writerTask;
    private bool _disposed;

    public ProducerConsumerFileWriter(string path, int bufferSize = 1 << 20)
    {
        _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, FileOptions.SequentialScan);
        _queue = new BlockingCollection<byte[]>(boundedCapacity: Environment.ProcessorCount * 2);
        _writerTask = Task.Run(WriterLoop);
    }

    private async Task WriterLoop()
    {
        foreach (var chunk in _queue.GetConsumingEnumerable())
        {
            await _fs.WriteAsync(chunk, 0, chunk.Length).ConfigureAwait(false);
        }
        await _fs.FlushAsync().ConfigureAwait(false);
    }

    public void EnqueueWrite(byte[] buffer, int count)
    {
        var data = new byte[count];
        Buffer.BlockCopy(buffer, 0, data, 0, count);
        _queue.Add(data);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _queue.CompleteAdding();
        _writerTask.Wait();
        _fs.Dispose();
        _queue.Dispose();
        _disposed = true;
    }
}