// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Graphics;

/// <summary>The kind of a recorded <see cref="Command"/>.</summary>
public enum CommandKind : byte
{
    /// <summary>Begins a render pass: <see cref="Command.Object"/> is the framebuffer, <see cref="Command.A"/> the index of its <see cref="RenderPassDescription"/>.</summary>
    BeginRenderPass,

    /// <summary>Ends the render pass.</summary>
    EndRenderPass,

    /// <summary>Binds a graphics pipeline: <see cref="Command.Object"/>.</summary>
    SetGraphicsPipeline,

    /// <summary>Binds a compute pipeline: <see cref="Command.Object"/>.</summary>
    SetComputePipeline,

    /// <summary>Binds a resource set: <see cref="Command.Object"/> at slot <see cref="Command.A"/>.</summary>
    SetResourceSet,

    /// <summary>Binds a vertex buffer: <see cref="Command.Object"/> at slot <see cref="Command.A"/>, from byte <see cref="Command.X"/>.</summary>
    SetVertexBuffer,

    /// <summary>Binds the index buffer: <see cref="Command.Object"/>, format <see cref="Command.A"/> (<see cref="IndexFormat"/>), from byte <see cref="Command.X"/>.</summary>
    SetIndexBuffer,

    /// <summary>Sets the viewport: x, y, width, height in <see cref="Command.A"/> to <see cref="Command.D"/> and the depth range in <see cref="Command.X"/>, as float bits; see <see cref="Command.GetViewport"/>.</summary>
    SetViewport,

    /// <summary>Sets the scissor rectangle: x, y, width, height in <see cref="Command.A"/> to <see cref="Command.D"/>.</summary>
    SetScissor,

    /// <summary>Draws: vertex count, instance count, first vertex, first instance in <see cref="Command.A"/> to <see cref="Command.D"/>.</summary>
    Draw,

    /// <summary>Draws indexed: index count, instance count, first index, first instance in <see cref="Command.A"/> to <see cref="Command.D"/>, base vertex in <see cref="Command.X"/>.</summary>
    DrawIndexed,

    /// <summary>Draws from arguments in <see cref="Command.Object"/> at byte <see cref="Command.X"/>: draw count <see cref="Command.A"/>, stride <see cref="Command.B"/>.</summary>
    DrawIndirect,

    /// <summary>Draws indexed from arguments in <see cref="Command.Object"/> at byte <see cref="Command.X"/>: draw count <see cref="Command.A"/>, stride <see cref="Command.B"/>.</summary>
    DrawIndexedIndirect,

    /// <summary>Dispatches <see cref="Command.A"/> × <see cref="Command.B"/> × <see cref="Command.C"/> work groups.</summary>
    Dispatch,

    /// <summary>Dispatches with arguments in <see cref="Command.Object"/> at byte <see cref="Command.X"/>.</summary>
    DispatchIndirect,

    /// <summary>Makes earlier shader writes visible to later commands: <see cref="Command.A"/> is the <see cref="BarrierScope"/>.</summary>
    Barrier,

    /// <summary>Writes bytes to a buffer: <see cref="Command.Object"/> at byte <see cref="Command.X"/>, data of length <see cref="Command.A"/> at <see cref="Command.Y"/> in the data of the command buffer.</summary>
    UpdateBuffer,

    /// <summary>Copies <see cref="Command.Y"/> bytes from <see cref="Command.Object"/> at byte <see cref="Command.X"/> to <see cref="Command.Object2"/> at byte <see cref="Command.Z"/>.</summary>
    CopyBuffer,

    /// <summary>Computes the mip levels of <see cref="Command.Object"/> from level 0.</summary>
    GenerateMipmaps,

    /// <summary>Opens a debug group named by the string <see cref="Command.Object"/>.</summary>
    PushDebugGroup,

    /// <summary>Closes the last debug group.</summary>
    PopDebugGroup,

    /// <summary>Inserts a debug marker named by the string <see cref="Command.Object"/>.</summary>
    InsertDebugMarker,
}

/// <summary>
/// One recorded command. A backend reads the commands of a
/// <see cref="CommandBuffer"/> when it is submitted; <see cref="Kind"/> says
/// what each field holds.
/// </summary>
[HotPath]
public readonly struct Command
{
    internal Command(CommandKind kind, int obj = -1, int obj2 = -1, int a = 0, int b = 0, int c = 0, int d = 0, long x = 0, long y = 0, long z = 0)
    {
        Kind = kind;
        Object = obj;
        Object2 = obj2;
        A = a;
        B = b;
        C = c;
        D = d;
        X = x;
        Y = y;
        Z = z;
    }

    /// <summary>Gets what the command does.</summary>
    public CommandKind Kind { get; }

    /// <summary>Gets the index of the first object of the command, for <see cref="CommandBuffer.GetObject{T}"/>; -1 for none.</summary>
    public int Object { get; }

    /// <summary>Gets the index of the second object of the command; -1 for none.</summary>
    public int Object2 { get; }

    /// <summary>Gets the first integer argument.</summary>
    public int A { get; }

    /// <summary>Gets the second integer argument.</summary>
    public int B { get; }

    /// <summary>Gets the third integer argument.</summary>
    public int C { get; }

    /// <summary>Gets the fourth integer argument.</summary>
    public int D { get; }

    /// <summary>Gets the first long argument.</summary>
    public long X { get; }

    /// <summary>Gets the second long argument.</summary>
    public long Y { get; }

    /// <summary>Gets the third long argument.</summary>
    public long Z { get; }

    /// <summary>Reads the viewport of a <see cref="CommandKind.SetViewport"/> command.</summary>
    /// <returns>The viewport.</returns>
    [HotPath]
    public Viewport GetViewport() => new(
        BitConverter.Int32BitsToSingle(A),
        BitConverter.Int32BitsToSingle(B),
        BitConverter.Int32BitsToSingle(C),
        BitConverter.Int32BitsToSingle(D),
        BitConverter.Int32BitsToSingle((int)X),
        BitConverter.Int32BitsToSingle((int)(X >> 32)));
}

/// <summary>
/// Records GPU commands, then hands them to the device with
/// <see cref="GraphicsDevice.Submit"/>. Recording checks each command at
/// once, and allocates nothing once the buffer has grown to the size of a
/// frame.
/// </summary>
/// <remarks>
/// Record on the loop thread of the client context. A command buffer is
/// recorded again after each submission: <see cref="Begin"/> clears it.
/// </remarks>
public sealed class CommandBuffer
{
    /// <summary>The largest number of resource layouts of a pipeline.</summary>
    public const int MaxResourceSets = 8;

    /// <summary>The largest number of vertex buffers of a pipeline.</summary>
    public const int MaxVertexBuffers = 16;

    private Command[] _commands = new Command[256];
    private object?[] _objects = new object?[256];
    private RenderPassDescription[] _passes = new RenderPassDescription[16];
    private byte[] _data = new byte[4096];
    private int _commandCount;
    private int _objectCount;
    private int _passCount;
    private int _dataLength;

    private bool _recording;
    private Framebuffer? _pass;
    private GraphicsPipeline? _graphics;
    private ComputePipeline? _compute;
    private int _setMask;
    private bool _indexBuffer;
    private bool _indexAtBlockStart;
    private int _debugDepth;

    internal CommandBuffer(GraphicsDevice device, string? name)
    {
        Device = device;
        Name = name;
    }

    /// <summary>Gets the device.</summary>
    public GraphicsDevice Device { get; }

    /// <summary>Gets the debug name, or <see langword="null"/>.</summary>
    public string? Name { get; }

    /// <summary>Gets whether commands are being recorded: between <see cref="Begin"/> and <see cref="End"/>.</summary>
    public bool IsRecording => _recording;

    /// <summary>Gets whether the commands are ready to submit: ended and not yet submitted.</summary>
    public bool IsReady { get; private set; }

    /// <summary>Gets the recorded commands, for the backend.</summary>
    public ReadOnlySpan<Command> Commands => _commands.AsSpan(0, _commandCount);

    /// <summary>Gets an object of a command, for the backend.</summary>
    /// <typeparam name="T">The type of the object.</typeparam>
    /// <param name="index">The index of <see cref="Command.Object"/> or <see cref="Command.Object2"/>.</param>
    /// <returns>The object.</returns>
    [HotPath]
    public T GetObject<T>(int index)
        where T : class => (T)_objects[index]!;

    /// <summary>Gets bytes of an <see cref="CommandKind.UpdateBuffer"/> command, for the backend.</summary>
    /// <param name="offset">The offset, <see cref="Command.Y"/>.</param>
    /// <param name="length">The length, <see cref="Command.A"/>.</param>
    /// <returns>The bytes.</returns>
    [HotPath]
    public ReadOnlySpan<byte> GetData(long offset, int length) => _data.AsSpan((int)offset, length);

    /// <summary>Gets how a render pass begins, for the backend.</summary>
    /// <param name="index">The index, <see cref="Command.A"/> of <see cref="CommandKind.BeginRenderPass"/>.</param>
    /// <returns>The description.</returns>
    [HotPath]
    public ref readonly RenderPassDescription GetRenderPass(int index) => ref _passes[index];

    /// <summary>Clears the buffer and starts recording.</summary>
    /// <exception cref="InvalidOperationException">The buffer is already recording.</exception>
    [HotPath]
    public void Begin()
    {
        if (_recording)
        {
            throw new InvalidOperationException("The command buffer is already recording: call End first.");
        }

        Array.Clear(_objects, 0, _objectCount);
        _commandCount = 0;
        _objectCount = 0;
        _passCount = 0;
        _dataLength = 0;
        _pass = null;
        _graphics = null;
        _compute = null;
        _setMask = 0;
        _indexBuffer = false;
        _debugDepth = 0;
        _recording = true;
        IsReady = false;
    }

    /// <summary>Stops recording: the commands are ready to submit.</summary>
    /// <exception cref="InvalidOperationException">A render pass or a debug group is still open.</exception>
    [HotPath]
    public void End()
    {
        CheckRecording();
        if (_pass is not null)
        {
            throw new InvalidOperationException("A render pass is still open: call EndRenderPass first.");
        }

        if (_debugDepth != 0)
        {
            throw new InvalidOperationException("A debug group is still open: call PopDebugGroup first.");
        }

        _recording = false;
        IsReady = true;
    }

    /// <summary>Begins a render pass on a framebuffer.</summary>
    /// <param name="framebuffer">The framebuffer, or the one of <see cref="GraphicsDevice.Swapchain"/>.</param>
    /// <param name="description">How the attachments begin.</param>
    /// <exception cref="InvalidOperationException">A pass is open, or the swapchain is not available this frame.</exception>
    [HotPath]
    public void BeginRenderPass(Framebuffer framebuffer, in RenderPassDescription description)
    {
        CheckRecording();
        CheckResource(framebuffer);
        if (_pass is not null)
        {
            throw new InvalidOperationException("A render pass is already open: render passes do not nest.");
        }

        if (framebuffer.IsSwapchain && !Device.Swapchain.IsAvailable)
        {
            throw new InvalidOperationException("The swapchain is not available this frame (the window is minimized, or its surface is lost): check the result of BeginFrame.");
        }

        if (_passCount == _passes.Length)
        {
            Array.Resize(ref _passes, _passes.Length * 2);
        }

        _passes[_passCount] = description;
        Add(new Command(CommandKind.BeginRenderPass, AddObject(framebuffer), a: _passCount));
        _passCount++;
        _pass = framebuffer;
        _graphics = null;
        _setMask = 0;
        _indexBuffer = false;
    }

    /// <summary>Ends the render pass.</summary>
    /// <exception cref="InvalidOperationException">No pass is open.</exception>
    [HotPath]
    public void EndRenderPass()
    {
        CheckInPass();
        Add(new Command(CommandKind.EndRenderPass));
        _pass = null;
        _graphics = null;
        _setMask = 0;
    }

    /// <summary>Binds a graphics pipeline, inside a render pass whose attachments match its outputs.</summary>
    /// <param name="pipeline">The pipeline.</param>
    /// <exception cref="InvalidOperationException">No pass is open, or the formats of the framebuffer differ from the outputs of the pipeline.</exception>
    [HotPath]
    public void SetPipeline(GraphicsPipeline pipeline)
    {
        CheckInPass();
        CheckResource(pipeline);
        if (!Matches(pipeline, _pass!))
        {
            throw new InvalidOperationException($"{pipeline} draws to other formats than the attachments of {_pass}.");
        }

        Add(new Command(CommandKind.SetGraphicsPipeline, AddObject(pipeline)));
        _graphics = pipeline;
        _setMask = 0;
    }

    /// <summary>Binds a compute pipeline, outside render passes.</summary>
    /// <param name="pipeline">The pipeline.</param>
    /// <exception cref="InvalidOperationException">A pass is open.</exception>
    [HotPath]
    public void SetPipeline(ComputePipeline pipeline)
    {
        CheckOutsidePass();
        CheckResource(pipeline);
        Add(new Command(CommandKind.SetComputePipeline, AddObject(pipeline)));
        _compute = pipeline;
        _setMask = 0;
    }

    /// <summary>Binds a resource set at a slot of the bound pipeline: the graphics one inside a pass, the compute one outside.</summary>
    /// <param name="slot">The slot, an index in the resource layouts of the pipeline.</param>
    /// <param name="set">The set; its layout is the layout of the pipeline at that slot.</param>
    /// <exception cref="InvalidOperationException">No pipeline is bound, or the layout differs.</exception>
    [HotPath]
    public void SetResourceSet(int slot, ResourceSet set)
    {
        CheckRecording();
        CheckResource(set);
        Pipeline pipeline = (_pass is not null ? (Pipeline?)_graphics : _compute)
            ?? throw new InvalidOperationException("Bind a pipeline before its resource sets.");
        ResourceLayout[] layouts = pipeline.LayoutArray;
        if ((uint)slot >= (uint)layouts.Length)
        {
            throw new InvalidOperationException($"{pipeline} has {layouts.Length} resource layouts; slot {slot} does not exist.");
        }

        if (!ReferenceEquals(layouts[slot], set.Layout))
        {
            throw new InvalidOperationException($"{set} has another layout than slot {slot} of {pipeline}.");
        }

        Add(new Command(CommandKind.SetResourceSet, AddObject(set), a: slot));
        _setMask |= 1 << slot;
    }

    /// <summary>Binds a vertex buffer at a slot, inside a render pass.</summary>
    /// <param name="slot">The slot, an index in the vertex buffer layouts of the pipeline.</param>
    /// <param name="buffer">A buffer with <see cref="BufferUsage.Vertex"/>.</param>
    /// <param name="offset">The first byte read.</param>
    /// <exception cref="InvalidOperationException">No pass is open, or the buffer is not a vertex buffer.</exception>
    [HotPath]
    public void SetVertexBuffer(int slot, GpuBuffer buffer, long offset = 0)
    {
        CheckInPass();
        CheckResource(buffer);
        if ((buffer.Usage & BufferUsage.Vertex) == 0 || (uint)slot >= MaxVertexBuffers || offset < 0 || offset >= buffer.Size)
        {
            throw new InvalidOperationException($"{buffer} cannot be bound as vertex buffer {slot} at byte {offset}: it needs BufferUsage.Vertex, a slot under {MaxVertexBuffers}, and an offset inside the buffer.");
        }

        Add(new Command(CommandKind.SetVertexBuffer, AddObject(buffer), a: slot, x: offset));
    }

    /// <summary>Binds the index buffer, inside a render pass.</summary>
    /// <param name="buffer">A buffer with <see cref="BufferUsage.Index"/>.</param>
    /// <param name="format">The type of the indices.</param>
    /// <param name="offset">The first byte read.</param>
    /// <exception cref="InvalidOperationException">No pass is open, or the buffer is not an index buffer.</exception>
    [HotPath]
    public void SetIndexBuffer(GpuBuffer buffer, IndexFormat format, long offset = 0)
    {
        CheckInPass();
        CheckResource(buffer);
        if ((buffer.Usage & BufferUsage.Index) == 0 || offset < 0 || offset >= buffer.Size)
        {
            throw new InvalidOperationException($"{buffer} cannot be bound as index buffer at byte {offset}: it needs BufferUsage.Index and an offset inside the buffer.");
        }

        Add(new Command(CommandKind.SetIndexBuffer, AddObject(buffer), a: (int)format, x: offset));
        _indexBuffer = true;
        _indexAtBlockStart = buffer.Offset == 0 && offset == 0;
    }

    /// <summary>Sets the viewport, inside a render pass. A pass begins with a viewport that covers the framebuffer.</summary>
    /// <param name="viewport">The viewport.</param>
    [HotPath]
    public void SetViewport(in Viewport viewport)
    {
        CheckInPass();
        long depth = (uint)BitConverter.SingleToInt32Bits(viewport.MinDepth) | ((long)BitConverter.SingleToInt32Bits(viewport.MaxDepth) << 32);
        Add(new Command(
            CommandKind.SetViewport,
            a: BitConverter.SingleToInt32Bits(viewport.X),
            b: BitConverter.SingleToInt32Bits(viewport.Y),
            c: BitConverter.SingleToInt32Bits(viewport.Width),
            d: BitConverter.SingleToInt32Bits(viewport.Height),
            x: depth));
    }

    /// <summary>Sets the scissor rectangle, inside a render pass; pipelines with <see cref="RasterizerState.ScissorTest"/> clip to it.</summary>
    /// <param name="rect">The rectangle.</param>
    [HotPath]
    public void SetScissor(in PixelRect rect)
    {
        CheckInPass();
        Add(new Command(CommandKind.SetScissor, a: rect.X, b: rect.Y, c: rect.Width, d: rect.Height));
    }

    /// <summary>Draws vertices with the bound graphics pipeline.</summary>
    /// <param name="vertexCount">The number of vertices.</param>
    /// <param name="instanceCount">The number of instances.</param>
    /// <param name="firstVertex">The first vertex.</param>
    /// <param name="firstInstance">The first instance.</param>
    /// <exception cref="InvalidOperationException">No pipeline is bound, or a resource set of the pipeline is not bound.</exception>
    [HotPath]
    public void Draw(int vertexCount, int instanceCount = 1, int firstVertex = 0, int firstInstance = 0)
    {
        CheckDraw();
        Add(new Command(CommandKind.Draw, a: vertexCount, b: instanceCount, c: firstVertex, d: firstInstance));
    }

    /// <summary>Draws indexed vertices with the bound graphics pipeline and index buffer.</summary>
    /// <param name="indexCount">The number of indices.</param>
    /// <param name="instanceCount">The number of instances.</param>
    /// <param name="firstIndex">The first index.</param>
    /// <param name="baseVertex">The value added to each index.</param>
    /// <param name="firstInstance">The first instance.</param>
    /// <exception cref="InvalidOperationException">No pipeline or no index buffer is bound, or a resource set is missing.</exception>
    [HotPath]
    public void DrawIndexed(int indexCount, int instanceCount = 1, int firstIndex = 0, int baseVertex = 0, int firstInstance = 0)
    {
        CheckDraw();
        if (!_indexBuffer)
        {
            throw new InvalidOperationException("Bind an index buffer before an indexed draw.");
        }

        Add(new Command(CommandKind.DrawIndexed, a: indexCount, b: instanceCount, c: firstIndex, d: firstInstance, x: baseVertex));
    }

    /// <summary>Draws with arguments read from a buffer. Requires <see cref="GraphicsFeatures.DrawIndirect"/>.</summary>
    /// <param name="arguments">A buffer with <see cref="BufferUsage.Indirect"/> that holds the arguments, four 32-bit integers per draw.</param>
    /// <param name="offset">The byte of the first arguments; a multiple of 4.</param>
    /// <param name="drawCount">The number of draws.</param>
    /// <param name="stride">The bytes between two arguments; 0 for packed arguments.</param>
    [HotPath]
    public void DrawIndirect(GpuBuffer arguments, long offset, int drawCount, int stride = 0)
    {
        CheckDraw();
        CheckIndirect(arguments, offset);
        Add(new Command(CommandKind.DrawIndirect, AddObject(arguments), a: drawCount, b: stride, x: offset));
    }

    /// <summary>
    /// Draws indexed with arguments read from a buffer. Requires
    /// <see cref="GraphicsFeatures.DrawIndirect"/>, and an index buffer
    /// created with <see cref="BufferDescription.Dedicated"/> and bound at
    /// offset 0: the first index of the arguments counts from the start of
    /// its memory block.
    /// </summary>
    /// <param name="arguments">A buffer with <see cref="BufferUsage.Indirect"/> that holds the arguments, five 32-bit integers per draw.</param>
    /// <param name="offset">The byte of the first arguments; a multiple of 4.</param>
    /// <param name="drawCount">The number of draws.</param>
    /// <param name="stride">The bytes between two arguments; 0 for packed arguments.</param>
    [HotPath]
    public void DrawIndexedIndirect(GpuBuffer arguments, long offset, int drawCount, int stride = 0)
    {
        CheckDraw();
        CheckIndirect(arguments, offset);
        if (!_indexBuffer)
        {
            throw new InvalidOperationException("Bind an index buffer before an indexed draw.");
        }

        if (!_indexAtBlockStart)
        {
            throw new InvalidOperationException("An indexed indirect draw reads indices from the start of a memory block: create the index buffer with BufferDescription.Dedicated and bind it at offset 0.");
        }

        Add(new Command(CommandKind.DrawIndexedIndirect, AddObject(arguments), a: drawCount, b: stride, x: offset));
    }

    /// <summary>Runs the bound compute pipeline, outside render passes.</summary>
    /// <param name="groupsX">The number of work groups along X.</param>
    /// <param name="groupsY">The number of work groups along Y.</param>
    /// <param name="groupsZ">The number of work groups along Z.</param>
    /// <exception cref="InvalidOperationException">A pass is open, no compute pipeline is bound, or a resource set is missing.</exception>
    [HotPath]
    public void Dispatch(int groupsX, int groupsY = 1, int groupsZ = 1)
    {
        CheckDispatch();
        int max = Device.Capabilities.MaxComputeWorkGroups;
        if (groupsX < 0 || groupsY < 0 || groupsZ < 0 || groupsX > max || groupsY > max || groupsZ > max)
        {
            throw new ArgumentOutOfRangeException(nameof(groupsX), $"A dispatch has from 0 to {max} work groups per axis.");
        }

        Add(new Command(CommandKind.Dispatch, a: groupsX, b: groupsY, c: groupsZ));
    }

    /// <summary>Runs the bound compute pipeline with group counts read from a buffer. Requires <see cref="GraphicsFeatures.DrawIndirect"/>.</summary>
    /// <param name="arguments">A buffer with <see cref="BufferUsage.Indirect"/> that holds three 32-bit integers.</param>
    /// <param name="offset">The byte of the arguments; a multiple of 4.</param>
    [HotPath]
    public void DispatchIndirect(GpuBuffer arguments, long offset)
    {
        CheckDispatch();
        CheckIndirect(arguments, offset);
        Add(new Command(CommandKind.DispatchIndirect, AddObject(arguments), x: offset));
    }

    /// <summary>Makes writes of earlier shaders visible to later commands, outside render passes.</summary>
    /// <param name="scope">The uses of the later commands that read the written data.</param>
    [HotPath]
    public void Barrier(BarrierScope scope)
    {
        CheckOutsidePass();
        Add(new Command(CommandKind.Barrier, a: (int)scope));
    }

    /// <summary>Writes bytes to a buffer when the commands run, outside render passes. The bytes are copied now.</summary>
    /// <param name="buffer">The buffer.</param>
    /// <param name="offset">The first byte written.</param>
    /// <param name="data">The bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException">The bytes do not fit in the buffer.</exception>
    [HotPath]
    public void UpdateBuffer(GpuBuffer buffer, long offset, ReadOnlySpan<byte> data)
    {
        CheckOutsidePass();
        CheckResource(buffer);
        if (offset < 0 || offset + data.Length > buffer.Size)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), $"{data.Length} bytes at byte {offset} do not fit in {buffer} of {buffer.Size} bytes.");
        }

        if (_dataLength + data.Length > _data.Length)
        {
            Array.Resize(ref _data, Math.Max(_data.Length * 2, _dataLength + data.Length));
        }

        data.CopyTo(_data.AsSpan(_dataLength));
        Add(new Command(CommandKind.UpdateBuffer, AddObject(buffer), a: data.Length, x: offset, y: _dataLength));
        _dataLength += data.Length;
    }

    /// <summary>Copies bytes between buffers, outside render passes.</summary>
    /// <param name="source">The buffer read.</param>
    /// <param name="sourceOffset">The first byte read.</param>
    /// <param name="destination">The buffer written.</param>
    /// <param name="destinationOffset">The first byte written.</param>
    /// <param name="size">The number of bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException">A range is outside its buffer.</exception>
    [HotPath]
    public void CopyBuffer(GpuBuffer source, long sourceOffset, GpuBuffer destination, long destinationOffset, long size)
    {
        CheckOutsidePass();
        CheckResource(source);
        CheckResource(destination);
        if (size <= 0 || sourceOffset < 0 || destinationOffset < 0 || sourceOffset + size > source.Size || destinationOffset + size > destination.Size)
        {
            throw new ArgumentOutOfRangeException(nameof(size), $"{size} bytes cannot be copied from byte {sourceOffset} of {source} to byte {destinationOffset} of {destination}.");
        }

        Add(new Command(CommandKind.CopyBuffer, AddObject(source), AddObject(destination), x: sourceOffset, y: size, z: destinationOffset));
    }

    /// <summary>Computes the mip levels of a texture from its level 0, outside render passes.</summary>
    /// <param name="texture">A texture with more than one mip level and a color format.</param>
    [HotPath]
    public void GenerateMipmaps(Texture texture)
    {
        CheckOutsidePass();
        CheckResource(texture);
        if (texture.MipLevels < 2 || PixelFormats.IsDepth(texture.Format))
        {
            throw new InvalidOperationException($"{texture} has no mip levels to compute: it needs more than one level and a color format.");
        }

        Add(new Command(CommandKind.GenerateMipmaps, AddObject(texture)));
    }

    /// <summary>Opens a debug group, which capture tools show around the commands until <see cref="PopDebugGroup"/>.</summary>
    /// <param name="name">The name of the group, such as the name of a pass; a string that lives on, such as a literal, so recording allocates nothing.</param>
    [HotPath]
    public void PushDebugGroup(string name)
    {
        CheckRecording();
        ArgumentNullException.ThrowIfNull(name);
        Add(new Command(CommandKind.PushDebugGroup, AddObject(name)));
        _debugDepth++;
    }

    /// <summary>Closes the last debug group.</summary>
    /// <exception cref="InvalidOperationException">No group is open.</exception>
    [HotPath]
    public void PopDebugGroup()
    {
        CheckRecording();
        if (_debugDepth == 0)
        {
            throw new InvalidOperationException("No debug group is open.");
        }

        Add(new Command(CommandKind.PopDebugGroup));
        _debugDepth--;
    }

    /// <summary>Inserts a debug marker, which capture tools show between commands.</summary>
    /// <param name="name">The text of the marker.</param>
    [HotPath]
    public void InsertDebugMarker(string name)
    {
        CheckRecording();
        ArgumentNullException.ThrowIfNull(name);
        Add(new Command(CommandKind.InsertDebugMarker, AddObject(name)));
    }

    /// <summary>Marks the commands as submitted; the device calls it.</summary>
    [HotPath]
    internal void MarkSubmitted() => IsReady = false;

    [HotPath]
    private static bool Matches(GraphicsPipeline pipeline, Framebuffer framebuffer)
    {
        PixelFormat[] wanted = pipeline.ColorFormats;
        PixelFormat[] actual = framebuffer.ColorFormatArray;
        if (wanted.Length != actual.Length || pipeline.DepthFormat != framebuffer.DepthFormat)
        {
            return false;
        }

        for (int i = 0; i < wanted.Length; i++)
        {
            if (wanted[i] != actual[i])
            {
                return false;
            }
        }

        return true;
    }

    [HotPath]
    private void Add(in Command command)
    {
        if (_commandCount == _commands.Length)
        {
            Array.Resize(ref _commands, _commands.Length * 2);
        }

        _commands[_commandCount++] = command;
    }

    [HotPath]
    private int AddObject(object value)
    {
        if (_objectCount == _objects.Length)
        {
            Array.Resize(ref _objects, _objects.Length * 2);
        }

        _objects[_objectCount] = value;
        return _objectCount++;
    }

    [HotPath]
    private void CheckRecording()
    {
        if (!_recording)
        {
            throw new InvalidOperationException("The command buffer is not recording: call Begin first.");
        }
    }

    [HotPath]
    private void CheckInPass()
    {
        CheckRecording();
        if (_pass is null)
        {
            throw new InvalidOperationException("This command runs inside a render pass: call BeginRenderPass first.");
        }
    }

    [HotPath]
    private void CheckOutsidePass()
    {
        CheckRecording();
        if (_pass is not null)
        {
            throw new InvalidOperationException("This command runs outside render passes: call EndRenderPass first.");
        }
    }

    [HotPath]
    private void CheckResource(GraphicsResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (resource.IsDisposed)
        {
            throw new ObjectDisposedException(resource.ToString(), "A disposed resource cannot be used in new commands.");
        }

        if (!ReferenceEquals(resource.Device, Device))
        {
            throw new InvalidOperationException($"{resource} belongs to another device.");
        }
    }

    [HotPath]
    private void CheckDraw()
    {
        CheckInPass();
        if (_graphics is null)
        {
            throw new InvalidOperationException("Bind a graphics pipeline before drawing.");
        }

        CheckSets(_graphics);
    }

    [HotPath]
    private void CheckDispatch()
    {
        CheckOutsidePass();
        if (_compute is null)
        {
            throw new InvalidOperationException("Bind a compute pipeline before dispatching.");
        }

        CheckSets(_compute);
    }

    [HotPath]
    private void CheckSets(Pipeline pipeline)
    {
        int all = (1 << pipeline.LayoutArray.Length) - 1;
        if ((_setMask & all) != all)
        {
            throw new InvalidOperationException($"{pipeline} has {pipeline.LayoutArray.Length} resource layouts: bind a resource set at each slot before drawing or dispatching.");
        }
    }

    [HotPath]
    private void CheckIndirect(GpuBuffer arguments, long offset)
    {
        if (!Device.Capabilities.Supports(GraphicsFeatures.DrawIndirect))
        {
            throw new NotSupportedException("Indirect draws and dispatches need the graphics feature DrawIndirect, which the device does not support.");
        }

        CheckResource(arguments);
        if ((arguments.Usage & BufferUsage.Indirect) == 0 || offset < 0 || (offset & 3) != 0 || offset >= arguments.Size)
        {
            throw new InvalidOperationException($"{arguments} cannot hold indirect arguments at byte {offset}: it needs BufferUsage.Indirect and an offset inside the buffer, multiple of 4.");
        }
    }
}
