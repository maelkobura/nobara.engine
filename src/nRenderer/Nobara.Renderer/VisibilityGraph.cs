// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Renderer;

/// <summary>
/// The visibility graph of a group: from the chunk of the camera, a search in
/// breadth across the faces of chunks, which crosses a section only between
/// two faces that its connectivity joins, and never turns back toward the
/// camera. Chunks without a section are open. The sections it reaches are
/// those the camera may see; the others are hidden behind sections that
/// block the view.
/// </summary>
/// <remarks>
/// The search covers the box of the sections of the group, one chunk wider on
/// each side. Its result depends only on the chunk of the camera and on the
/// sections, so it is kept until one of them changes. A camera outside the box
/// sees every section: the graph does not apply.
/// </remarks>
internal sealed class VisibilityGraph
{
    private readonly Dictionary<SectionOrigin, int> _sections = [];
    private int[] _cells = [];
    private byte[] _visited = [];
    private int[] _queue = [];
    private byte[] _entered = [];
    private byte[] _directions = [];
    private int[] _reached = [];
    private int _reachedCount;
    private int _minX, _minY, _minZ, _sizeX, _sizeY, _sizeZ;
    private bool _dirty = true;
    private bool _searched;
    private SectionOrigin _camera;

    /// <summary>Gets the number of sections in the graph.</summary>
    public int Count
    {
        [HotPath]
        get => _sections.Count;
    }

    /// <summary>Gets the slots of the sections of the group.</summary>
    public Dictionary<SectionOrigin, int>.ValueCollection Slots
    {
        [HotPath]
        get => _sections.Values;
    }

    /// <summary>Gets the slots of the sections reached by the last search.</summary>
    public ReadOnlySpan<int> Reached
    {
        [HotPath]
        get => _reached.AsSpan(0, _reachedCount);
    }

    /// <summary>Gets whether a section is at an origin of the group.</summary>
    /// <param name="origin">The origin.</param>
    /// <returns><see langword="true"/> when one is.</returns>
    public bool Contains(SectionOrigin origin) => _sections.ContainsKey(origin);

    /// <summary>Adds a section.</summary>
    /// <param name="origin">Its origin.</param>
    /// <param name="slot">Its slot.</param>
    public void Add(SectionOrigin origin, int slot)
    {
        _sections.Add(origin, slot);
        _dirty = true;
    }

    /// <summary>Removes a section.</summary>
    /// <param name="origin">Its origin.</param>
    public void Remove(SectionOrigin origin)
    {
        _sections.Remove(origin);
        _dirty = true;
    }

    /// <summary>Marks the connectivity of a section as changed.</summary>
    [HotPath]
    public void Invalidate() => _searched = false;

    /// <summary>Runs the search from the chunk of the camera, unless its last result still holds.</summary>
    /// <param name="camera">The chunk of the camera, in the group.</param>
    /// <param name="connectivity">The connectivity of each section, by slot.</param>
    /// <returns><see langword="false"/> when the camera is outside the box of the sections: every section is visible.</returns>
    public bool Search(SectionOrigin camera, ReadOnlySpan<SectionConnectivity> connectivity)
    {
        if (_dirty)
        {
            Rebuild();
        }

        return Traverse(camera, connectivity);
    }

    [HotPath]
    private bool Traverse(SectionOrigin camera, ReadOnlySpan<SectionConnectivity> connectivity)
    {
        int cx = camera.X - _minX;
        int cy = camera.Y - _minY;
        int cz = camera.Z - _minZ;
        if (_sections.Count == 0 || (uint)cx >= (uint)_sizeX || (uint)cy >= (uint)_sizeY || (uint)cz >= (uint)_sizeZ)
        {
            return false;
        }

        if (_searched && camera == _camera)
        {
            return true;
        }

        _camera = camera;
        _searched = true;
        Array.Clear(_visited);
        _reachedCount = 0;
        int strideY = _sizeX;
        int strideZ = _sizeX * _sizeY;
        int start = cx + (cy * strideY) + (cz * strideZ);
        int head = 0;
        int tail = 0;
        _visited[start] = 1;
        _queue[tail] = start;
        _entered[tail] = 255;
        _directions[tail] = 0;
        tail++;
        while (head < tail)
        {
            int cell = _queue[head];
            int entered = _entered[head];
            int directions = _directions[head];
            head++;
            int slot = _cells[cell] - 1;
            if (slot >= 0)
            {
                _reached[_reachedCount++] = slot;
            }

            int x = cell % _sizeX;
            int y = cell / strideY % _sizeY;
            int z = cell / strideZ;
            for (int face = 0; face < 6; face++)
            {
                // Never back toward the camera.
                if ((directions & (1 << (face ^ 1))) != 0)
                {
                    continue;
                }

                if (slot >= 0 && entered != 255 && !connectivity[slot].IsConnected((SectionFace)entered, (SectionFace)face))
                {
                    continue;
                }

                int nx = x + (face == 0 ? -1 : face == 1 ? 1 : 0);
                int ny = y + (face == 2 ? -1 : face == 3 ? 1 : 0);
                int nz = z + (face == 4 ? -1 : face == 5 ? 1 : 0);
                if ((uint)nx >= (uint)_sizeX || (uint)ny >= (uint)_sizeY || (uint)nz >= (uint)_sizeZ)
                {
                    continue;
                }

                int next = nx + (ny * strideY) + (nz * strideZ);
                if (_visited[next] != 0)
                {
                    continue;
                }

                _visited[next] = 1;
                _queue[tail] = next;
                _entered[tail] = (byte)(face ^ 1);
                _directions[tail] = (byte)(directions | (1 << face));
                tail++;
            }
        }

        return true;
    }

    // Lays the sections out in a dense box, one chunk wider on each side.
    private void Rebuild()
    {
        _dirty = false;
        _searched = false;
        if (_sections.Count == 0)
        {
            return;
        }

        int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;
        foreach (SectionOrigin origin in _sections.Keys)
        {
            minX = Math.Min(minX, origin.X);
            minY = Math.Min(minY, origin.Y);
            minZ = Math.Min(minZ, origin.Z);
            maxX = Math.Max(maxX, origin.X);
            maxY = Math.Max(maxY, origin.Y);
            maxZ = Math.Max(maxZ, origin.Z);
        }

        _minX = minX - 1;
        _minY = minY - 1;
        _minZ = minZ - 1;
        _sizeX = maxX - minX + 3;
        _sizeY = maxY - minY + 3;
        _sizeZ = maxZ - minZ + 3;
        long cells = (long)_sizeX * _sizeY * _sizeZ;
        if (cells > Array.MaxLength / 4)
        {
            throw new InvalidOperationException($"The sections of a group span {cells} chunks: too many for the visibility graph. Turn it off with ChunkGeometry.VisibilityGraph.");
        }

        if (_cells.Length < cells)
        {
            _cells = new int[cells];
            _visited = new byte[cells];
            _queue = new int[cells];
            _entered = new byte[cells];
            _directions = new byte[cells];
        }
        else
        {
            Array.Clear(_cells);
        }

        if (_reached.Length < _sections.Count)
        {
            _reached = new int[Math.Max(_sections.Count, _reached.Length * 2)];
        }

        foreach ((SectionOrigin origin, int slot) in _sections)
        {
            int index = (origin.X - _minX) + ((origin.Y - _minY) * _sizeX) + ((origin.Z - _minZ) * _sizeX * _sizeY);
            _cells[index] = slot + 1;
        }
    }
}
