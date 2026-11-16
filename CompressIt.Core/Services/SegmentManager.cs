using CompressIt.Core.Models;

namespace CompressIt.Core.Services;

public static class SegmentManager
{
    public static readonly TimeSpan DefaultMinSegmentDuration = TimeSpan.FromMilliseconds(250);

    public static List<VideoSegment> CreateInitial(TimeSpan totalDuration)
    {
        if (totalDuration <= TimeSpan.Zero)
        {
            return [];
        }

        return
        [
            new VideoSegment
            {
                Index = 1,
                Start = TimeSpan.Zero,
                End = totalDuration,
                IsIncluded = true
            }
        ];
    }

    public static List<VideoSegment> SplitAt(
        IReadOnlyList<VideoSegment> segments,
        TimeSpan playhead,
        TimeSpan? minSegmentDuration = null)
    {
        var minDur = minSegmentDuration ?? DefaultMinSegmentDuration;
        var result = new List<VideoSegment>(segments.Count + 1);
        bool didSplit = false;

        foreach (var seg in segments)
        {
            if (!didSplit &&
                playhead - seg.Start >= minDur &&
                seg.End - playhead >= minDur)
            {
                result.Add(new VideoSegment
                {
                    Start = seg.Start,
                    End = playhead,
                    IsIncluded = seg.IsIncluded
                });
                result.Add(new VideoSegment
                {
                    Start = playhead,
                    End = seg.End,
                    IsIncluded = seg.IsIncluded
                });
                didSplit = true;
            }
            else
            {
                result.Add(new VideoSegment
                {
                    Id = seg.Id,
                    Start = seg.Start,
                    End = seg.End,
                    IsIncluded = seg.IsIncluded
                });
            }
        }

        Reindex(result);
        return result;
    }

    public static bool CanSplitAt(
        IReadOnlyList<VideoSegment> segments,
        TimeSpan playhead,
        TimeSpan? minSegmentDuration = null)
    {
        var minDur = minSegmentDuration ?? DefaultMinSegmentDuration;
        return segments.Any(seg =>
            playhead - seg.Start >= minDur &&
            seg.End - playhead >= minDur);
    }

    public static List<VideoSegment> ToggleInclude(IReadOnlyList<VideoSegment> segments, Guid segmentId)
    {
        var result = segments.Select(s => new VideoSegment
        {
            Id = s.Id,
            Index = s.Index,
            Start = s.Start,
            End = s.End,
            IsIncluded = s.Id == segmentId ? !s.IsIncluded : s.IsIncluded
        }).ToList();

        Reindex(result);
        return result;
    }

    public static List<VideoSegment> MergeWithNext(IReadOnlyList<VideoSegment> segments, Guid segmentId)
    {
        var list = segments.Select(s => new VideoSegment
        {
            Id = s.Id,
            Start = s.Start,
            End = s.End,
            IsIncluded = s.IsIncluded
        }).ToList();

        int idx = list.FindIndex(s => s.Id == segmentId);
        if (idx >= 0 && idx < list.Count - 1)
        {
            list[idx].End = list[idx + 1].End;
            list[idx].IsIncluded = list[idx].IsIncluded || list[idx + 1].IsIncluded;
            list.RemoveAt(idx + 1);
        }

        Reindex(list);
        return list;
    }

    public static TimeSpan GetTotalIncludedDuration(IEnumerable<VideoSegment> segments)
    {
        var total = TimeSpan.Zero;
        foreach (var seg in segments)
        {
            if (seg.IsIncluded)
            {
                total += seg.Duration;
            }
        }
        return total;
    }

    private static void Reindex(List<VideoSegment> segments)
    {
        for (int i = 0; i < segments.Count; i++)
        {
            segments[i].Index = i + 1;
        }
    }
}
