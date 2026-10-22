namespace PlcMonitor.Drivers.Modbus.Tests;

/// <summary>Cable serie simulado en memoria: lo que escribe un extremo lo lee el otro.</summary>
internal sealed class FakeSerialLine
{
    public FakeSerialLine(string portName = "COM-SIM")
    {
        Master = new End(portName);
        Slave = new End(portName + "-esclavo");
        Master.Peer = Slave;
        Slave.Peer = Master;
    }

    /// <summary>Extremo del agente (lo abre el bus serie).</summary>
    public End Master { get; }

    /// <summary>Extremo de los esclavos (ModbusRtuServer).</summary>
    public End Slave { get; }

    internal sealed class End(string portName) : IRtuSerialPort
    {
        private readonly Queue<byte> _in = new();
        private readonly SemaphoreSlim _signal = new(0);

        public End? Peer { get; set; }

        public string PortName { get; } = portName;

        public bool IsOpen { get; private set; }

        public int TimeoutMs { get; set; } = 1000;

        public int OpenCount { get; private set; }

        public void Open()
        {
            IsOpen = true;
            OpenCount++;
        }

        public void Close() => IsOpen = false;

        public void Dispose() => IsOpen = false;

        public void DiscardInBuffer()
        {
            lock (_in)
                _in.Clear();
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            var peer = Peer!;
            lock (peer._in)
            {
                foreach (var b in buffer.AsSpan(offset, count))
                    peer._in.Enqueue(b);
            }
            peer._signal.Release();
        }

        public Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            while (true)
            {
                lock (_in)
                {
                    if (_in.Count > 0)
                    {
                        var n = 0;
                        while (n < count && _in.Count > 0)
                            buffer[offset + n++] = _in.Dequeue();
                        return n;
                    }
                }
                if (!await _signal.WaitAsync(TimeoutMs, token))
                    throw new TimeoutException("Sin datos en el cable simulado.");
            }
        }
    }
}
