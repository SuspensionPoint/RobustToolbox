using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading.Tasks;
using Robust.Client.ComponentTrees;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Graphics;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Threading;
using Robust.Shared.Utility;

namespace Robust.Client.Graphics.Clyde;

// this partial class contains code specific to querying, processing & sorting sprites.
internal partial class Clyde
{
    [Shared.IoC.Dependency] private readonly IParallelManager _parMan = default!;
    private readonly RefList<SpriteData> _drawingSpriteList = new();
    private const int _spriteProcessingBatchSize = 25;

    // LOD culling: squared minimum screen-space size threshold (pixels²).
    // Set via render.min_sprite_size CVar. 0 = disabled.
    private float _minSpriteSizeSquared;

    // Bucket sort toggle, set via render.bucket_sort CVar.
    private bool _useBucketSort;

    // DrawDepth range constants for bucket sort.
    // Content DrawDepth enum: LowFloors = -20, Overlays = +14.
    // We add a margin of 1 on each side for safety.
    private const int DrawDepthMin = -21;
    private const int DrawDepthMax = 15;
    private const int DrawDepthRange = DrawDepthMax - DrawDepthMin + 1; // 37

    // Reusable bucket arrays to avoid per-frame allocation.
    // Buckets are indexed by (drawDepth - DrawDepthMin).
    // Each bucket stores indices into _drawingSpriteList.
    private int[] _bucketCounts = new int[DrawDepthRange];
    private int[] _bucketOffsets = new int[DrawDepthRange];

    private void GetSprites(MapId map, Viewport view, IEye eye, Box2Rotated worldBounds, out int[] indexList)
    {
        ProcessSpriteEntities(map, view, eye, worldBounds, _drawingSpriteList);

        var totalCount = _drawingSpriteList.Count;

        // We use a separate list for indexing sprites so that the sort is faster.
        indexList = ArrayPool<int>.Shared.Rent(totalCount);

        // Populate index list, optionally filtering by screen-space size (LOD culling).
        int filteredCount;
        if (_minSpriteSizeSquared > 0)
        {
            filteredCount = 0;
            for (var i = 0; i < totalCount; i++)
            {
                ref var data = ref _drawingSpriteList[i];
                var bb = data.SpriteScreenBB;
                var w = bb.Right - bb.Left;
                var h = bb.Top - bb.Bottom;
                if (w * w + h * h >= _minSpriteSizeSquared)
                {
                    indexList[filteredCount++] = i;
                }
            }
        }
        else
        {
            filteredCount = totalCount;
            for (var i = 0; i < totalCount; i++)
                indexList[i] = i;
        }

        // Sort index list.
        if (_useBucketSort && filteredCount > 0)
        {
            BucketSortSprites(indexList, filteredCount);
        }
        else
        {
            Array.Sort(indexList, 0, filteredCount, new SpriteDrawingOrderComparer(_drawingSpriteList));
        }

        // Store the filtered count so callers know how many valid entries there are.
        // The existing code uses _drawingSpriteList.Count for iteration bounds after GetSprites.
        // We overwrite _spriteFilteredCount for use by the caller.
        _spriteFilteredCount = filteredCount;
    }

    /// <summary>
    /// Number of sprites that passed LOD filtering in the last GetSprites call.
    /// Used instead of _drawingSpriteList.Count when LOD culling is active.
    /// </summary>
    internal int _spriteFilteredCount;

    /// <summary>
    /// Bucket sort: groups sprites by DrawDepth, then sorts within each bucket by
    /// RenderOrder, Y position, and EntityUid. Much faster than a full comparison sort
    /// when many sprites are visible, because DrawDepth has a small integer range.
    /// </summary>
    private void BucketSortSprites(int[] indexList, int count)
    {
        var list = _drawingSpriteList;

        // Phase 1: Count entries per DrawDepth bucket.
        Array.Clear(_bucketCounts, 0, DrawDepthRange);

        for (var i = 0; i < count; i++)
        {
            var depth = list[indexList[i]].Sprite.DrawDepth;
            var bucket = Math.Clamp(depth - DrawDepthMin, 0, DrawDepthRange - 1);
            _bucketCounts[bucket]++;
        }

        // Phase 2: Compute bucket offsets (prefix sum).
        _bucketOffsets[0] = 0;
        for (var b = 1; b < DrawDepthRange; b++)
        {
            _bucketOffsets[b] = _bucketOffsets[b - 1] + _bucketCounts[b - 1];
        }

        // Phase 3: Scatter indices into bucket positions using a temporary array.
        var scattered = ArrayPool<int>.Shared.Rent(count);
        // Copy bucket offsets so we can increment them during scatter.
        var writePos = ArrayPool<int>.Shared.Rent(DrawDepthRange);
        Array.Copy(_bucketOffsets, writePos, DrawDepthRange);

        for (var i = 0; i < count; i++)
        {
            var idx = indexList[i];
            var depth = list[idx].Sprite.DrawDepth;
            var bucket = Math.Clamp(depth - DrawDepthMin, 0, DrawDepthRange - 1);
            scattered[writePos[bucket]++] = idx;
        }

        // Copy scattered back into indexList.
        Array.Copy(scattered, indexList, count);
        ArrayPool<int>.Shared.Return(scattered);
        ArrayPool<int>.Shared.Return(writePos);

        // Phase 4: Sort within each non-empty bucket by RenderOrder, Y, Uid.
        var comparer = new SpriteIntraBucketComparer(list);
        for (var b = 0; b < DrawDepthRange; b++)
        {
            var bucketCount = _bucketCounts[b];
            if (bucketCount > 1)
            {
                Array.Sort(indexList, _bucketOffsets[b], bucketCount, comparer);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ProcessSpriteEntities(MapId map, Viewport view, IEye eye, Box2Rotated worldBounds, RefList<SpriteData> list)
    {
        var query = _entityManager.GetEntityQuery<TransformComponent>();
        var viewScale = eye.Scale * view.RenderScale * new Vector2(EyeManager.PixelsPerMeter, -EyeManager.PixelsPerMeter);
        var treeData = new BatchData()
        {
            Sys = _entityManager.EntitySysManager.GetEntitySystem<TransformSystem>(),
            Query = query,
            ViewRotation = eye.Rotation,
            ViewScale = viewScale,
            PreScaleViewOffset = view.Size / 2f / viewScale,
            ViewPosition = eye.Position.Position + eye.Offset
        };

        // We need to batch the actual tree query, or alternatively we need just get the list of sprites and then
        // parallelize the rotation & bounding box calculations.
        var index = 0;
        var added = 0;
        var opts = new ParallelOptions { MaxDegreeOfParallelism = _parMan.ParallelProcessCount };

        foreach (var (treeOwner, comp) in _spriteTreeSystem.GetIntersectingTrees(map, worldBounds))
        {
            var treeXform = query.GetComponent(treeOwner);
            var bounds = _transformSystem.GetInvWorldMatrix(treeOwner).TransformBox(worldBounds);
            DebugTools.Assert(treeXform.MapUid == treeXform.ParentUid || !treeXform.ParentUid.IsValid());

            treeData = treeData with
            {
                TreeOwner = treeOwner,
                TreePos = treeXform.LocalPosition,
                TreeRot = treeXform.LocalRotation,
                Sin = MathF.Sin((float)treeXform.LocalRotation),
                Cos = MathF.Cos((float)treeXform.LocalRotation),
            };

            comp.Tree.QueryAabb(ref list,
                static (ref RefList<SpriteData> state, in ComponentTreeEntry<SpriteComponent> value) =>
                {
                    ref var entry = ref state.AllocAdd();
                    entry.Uid = value.Uid;
                    entry.Sprite = value.Component;
                    entry.Xform = value.Transform;
                    return true;
                }, bounds, true);

            // Get bounding boxes & world positions
            added = list.Count - index;
            var batches = added/_spriteProcessingBatchSize;

            // TODO also do sorting here & use a merge sort later on for y-sorting?
            if (batches > 1)
                Parallel.For(0, batches, opts, (i) => ProcessSprites(list, index + i * _spriteProcessingBatchSize, _spriteProcessingBatchSize, treeData));
            else
                batches = 0;

            var remainder = added - _spriteProcessingBatchSize * batches;
            if (remainder > 0)
                ProcessSprites(list, index + batches * _spriteProcessingBatchSize, remainder, treeData);

            index += batches * _spriteProcessingBatchSize + remainder;
        }
    }

    /// <summary>
    ///     This function computes a sprites world position, rotation, and screen-space bounding box. The position &
    ///     rotation are required in general, but the bounding box is only really needed for y-sorting & if the
    ///     sprite has a post processing shader.
    /// </summary>
    private void ProcessSprites(
        RefList<SpriteData> list,
        int startIndex,
        int count,
        in BatchData batch)
    {
        for (int i = startIndex; i < startIndex + count; i++)
        {
            ref var data = ref list[i];
            DebugTools.Assert(data.Sprite.Visible);

            // To help explain the remainder of this function, it should be functionally equivalent to the following
            // three lines of code, but has been expanded & simplified to speed up the calculation:
            //
            // (data.WorldPos, data.WorldRot) = batch.Sys.GetWorldPositionRotation(data.Xform, batch.Query);
            // var spriteWorldBB = data.Sprite.CalculateRotatedBoundingBox(data.WorldPos, data.WorldRot, batch.ViewRotation);
            // data.SpriteScreenBB = Viewport.GetWorldToLocalMatrix().TransformBox(spriteWorldBB);

            var (pos, rot) = batch.Sys.GetRelativePositionRotation(data.Xform, batch.TreeOwner, batch.Query);
            pos = new Vector2(
                batch.TreePos.X + batch.Cos * pos.X - batch.Sin * pos.Y,
                batch.TreePos.Y + batch.Sin * pos.X + batch.Cos * pos.Y);

            rot += batch.TreeRot;
            data.WorldRot = rot;
            data.WorldPos = pos;

            var finalRotation = (float) (data.Sprite.NoRotation
                ? data.Sprite.Rotation
                : data.Sprite.Rotation + rot + batch.ViewRotation);

            // false for 99.9% of sprites
            if (data.Sprite.Offset != Vector2.Zero)
            {
                pos += data.Sprite.NoRotation
                    ? (-batch.ViewRotation).RotateVec(data.Sprite.Offset)
                    : rot.RotateVec(data.Sprite.Offset);
            }

            pos = batch.ViewRotation.RotateVec(pos - batch.ViewPosition);

            // special casing angle = n*pi/2 to avoid box rotation & bounding calculations doesn't seem to give significant speedups.
            data.SpriteScreenBB = TransformCenteredBox(
                _spriteSystem.GetLocalBounds((data.Uid, data.Sprite)),
                finalRotation,
                pos + batch.PreScaleViewOffset,
                batch.ViewScale);
        }
    }

    /// <summary>
    /// This is effectively a specialized combination of a <see cref="Matrix3Helpers.TransformBox(Matrix3x2, in Box2)"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe Box2 TransformCenteredBox(in Box2 box, float angle, in Vector2 offset, in Vector2 scale)
    {
        var boxVec = Unsafe.As<Box2, Vector128<float>>(ref Unsafe.AsRef(in box));
        var sin = Vector128.Create(MathF.Sin(angle));
        var cos = Vector128.Create(MathF.Cos(angle));
        var boxX = Vector128.Shuffle(boxVec, Vector128.Create(0, 0, 2, 2));
        var boxY = Vector128.Shuffle(boxVec, Vector128.Create(1, 3, 3, 1));

        var x = boxX * cos - boxY * sin;
        var y = boxX * sin + boxY * cos;
        var lbrt = SimdHelpers.GetAABB(x, y);

        // This function is for sprites, which flip the y-axis via the scale, so we need to flip t & b.
        DebugTools.Assert(scale.Y < 0);
        lbrt = Vector128.Shuffle(lbrt, Vector128.Create(0,3,2,1));

        var offsetVec = Unsafe.As<Vector2, Vector128<float>>(ref Unsafe.AsRef(in offset)); // upper undefined
        var scaleVec = Unsafe.As<Vector2, Vector128<float>>(ref Unsafe.AsRef(in scale)); // upper undefined
        offsetVec = Vector128.Shuffle(offsetVec, Vector128.Create(0, 1, 0, 1));
        scaleVec = Vector128.Shuffle(scaleVec, Vector128.Create(0, 1, 0, 1));

        // offset and scale box.
        // note that the scaling here is scaling the whole space, not jut the box. I.e., the centre of the box is changing
        lbrt = (lbrt + offsetVec) * scaleVec;
        return Unsafe.As<Vector128<float>, Box2>(ref lbrt);
    }

    private struct SpriteData
    {
        public EntityUid Uid;
        public SpriteComponent Sprite;
        public TransformComponent Xform;
        public Vector2 WorldPos;
        public Angle WorldRot;
        public Box2 SpriteScreenBB;
    }

    private readonly struct BatchData
    {
        public TransformSystem Sys { get; init; }
        public EntityQuery<TransformComponent> Query { get; init; }
        public Angle ViewRotation { get; init; }
        public Vector2 ViewScale { get; init; }
        public Vector2 PreScaleViewOffset { get; init; }
        public Vector2 ViewPosition { get; init; }
        public EntityUid TreeOwner { get; init; }
        public Vector2 TreePos { get; init; }
        public Angle TreeRot { get; init; }
        public float Sin { get; init; }
        public float Cos { get;  init; }
    }

    /// <summary>
    /// Full comparison sort comparer — used as fallback when bucket sort is disabled.
    /// Compares by DrawDepth, RenderOrder, Y position, then EntityUid.
    /// </summary>
    private sealed class SpriteDrawingOrderComparer : IComparer<int>
    {
        private readonly RefList<SpriteData> _drawList;

        public SpriteDrawingOrderComparer(RefList<SpriteData> drawList)
        {
            _drawList = drawList;
        }

        public int Compare(int x, int y)
        {
            var a = _drawList[x];
            var b = _drawList[y];

            var cmp = a.Sprite.DrawDepth.CompareTo(b.Sprite.DrawDepth);
            if (cmp != 0)
                return cmp;

            cmp = a.Sprite.RenderOrder.CompareTo(b.Sprite.RenderOrder);

            if (cmp != 0)
                return cmp;

            // compare the top of the sprite's BB for y-sorting. Because screen coordinates are flipped, the "top" of the BB is actually the "bottom".
            cmp = a.SpriteScreenBB.Top.CompareTo(b.SpriteScreenBB.Top);

            if (cmp != 0)
                return cmp;

            return a.Uid.CompareTo(b.Uid);
        }
    }

    /// <summary>
    /// Intra-bucket comparer for the bucket sort path. Since all entries in a bucket share the same
    /// DrawDepth, this only compares RenderOrder, Y position, and EntityUid.
    /// </summary>
    private sealed class SpriteIntraBucketComparer : IComparer<int>
    {
        private readonly RefList<SpriteData> _drawList;

        public SpriteIntraBucketComparer(RefList<SpriteData> drawList)
        {
            _drawList = drawList;
        }

        public int Compare(int x, int y)
        {
            var a = _drawList[x];
            var b = _drawList[y];

            var cmp = a.Sprite.RenderOrder.CompareTo(b.Sprite.RenderOrder);
            if (cmp != 0)
                return cmp;

            cmp = a.SpriteScreenBB.Top.CompareTo(b.SpriteScreenBB.Top);
            if (cmp != 0)
                return cmp;

            return a.Uid.CompareTo(b.Uid);
        }
    }
}
