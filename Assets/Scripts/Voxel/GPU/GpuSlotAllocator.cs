/*
using System.Collections.Generic;
using UnityEngine;

namespace TerraVoxel.Voxel.GPU
{
    /// <summary>
    /// Fixed-size slot allocator with generation IDs for use-after-free safety.
    /// Used by GpuWorldState to allocate/free chunk slots. Free increments generation.
    /// </summary>
    public sealed class GpuSlotAllocator
    {
        readonly int _maxSlots;
        readonly Stack<int> _freeList;
        readonly uint[] _generationIds;
        int _allocatedCount;

        public int MaxSlots => _maxSlots;
        public int AllocatedCount => _allocatedCount;
        public int FreeCount => _freeList.Count;

        public GpuSlotAllocator(int maxSlots)
        {
            _maxSlots = Mathf.Max(1, maxSlots);
            _freeList = new Stack<int>(_maxSlots);
            _generationIds = new uint[_maxSlots];
            for (int i = _maxSlots - 1; i >= 0; i--)
                _freeList.Push(i);
        }

        /// <summary>Allocate a slot. Returns (slotIndex, generation). Throws if no free slots.</summary>
        public (int slot, uint generation) Allocate()
        {
            if (_freeList.Count == 0)
                throw new System.InvalidOperationException($"[GpuSlotAllocator] No free slots (max={_maxSlots})");
            int slot = _freeList.Pop();
            uint gen = _generationIds[slot];
            _allocatedCount++;
            return (slot, gen);
        }

        /// <summary>Try to allocate a slot. Returns true and (slot, generation) on success; false and (-1, 0) when full. Use instead of Allocate to avoid exceptions in production.</summary>
        public bool TryAllocate(out int slot, out uint generation)
        {
            if (_freeList.Count == 0)
            {
                slot = -1;
                generation = 0;
                return false;
            }
            slot = _freeList.Pop();
            generation = _generationIds[slot];
            _allocatedCount++;
            return true;
        }

        /// <summary>Free a slot. Increments generation so previous users detect use-after-free.</summary>
        public void Free(int slot)
        {
            if (slot < 0 || slot >= _maxSlots)
            {
                Debug.LogWarning($"[GpuSlotAllocator] Free out of range slot {slot}");
                return;
            }
            _generationIds[slot]++;
            _freeList.Push(slot);
            _allocatedCount--;
        }

        /// <summary>Returns true if (slot, generation) still refers to a valid allocated slot.</summary>
        public bool IsValid(int slot, uint generation)
        {
            if (slot < 0 || slot >= _maxSlots) return false;
            return _generationIds[slot] == generation;
        }

        /// <summary>Get current generation for a slot (for descriptor upload).</summary>
        public uint GetGeneration(int slot)
        {
            if (slot < 0 || slot >= _maxSlots) return 0;
            return _generationIds[slot];
        }
    }
}
*/

using System.Collections.Generic;
using UnityEngine;

namespace TerraVoxel.Voxel.GPU
{
    public sealed class GpuSlotAllocator
    {
        public GpuSlotAllocator(int maxSlots) { }
        public int MaxSlots => 0;
        public int AllocatedCount => 0;
        public int FreeCount => 0;
        public (int slot, uint generation) Allocate() => (-1, 0);
        public bool TryAllocate(out int slot, out uint generation) { slot = -1; generation = 0; return false; }
        public void Free(int slot) { }
        public bool IsValid(int slot, uint generation) => false;
        public uint GetGeneration(int slot) => 0;
    }
}