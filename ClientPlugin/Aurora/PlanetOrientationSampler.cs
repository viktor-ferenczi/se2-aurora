using System.Collections.Generic;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Voxels.Components;

namespace ClientPlugin.Aurora;

/// <summary>
/// Game-thread side: the renderer knows every planet's position and atmosphere, but not its
/// orientation, which decides where the poles are. This samples the planet transforms from
/// the session's planet tracker and publishes an immutable snapshot for the render thread,
/// which matches entries to render-side planets by position.
/// </summary>
public static class PlanetOrientationSampler
{
    // Every half second of simulation; planets do not move.
    private const int UpdateInterval = 30;

    // Planets are matched by position; anything further apart than this is a different planet.
    private const double MatchDistance = 1000.0;

    private sealed class Entry
    {
        public Vector3D Position;
        public Vector3 Up;
    }

    private static volatile Entry[] snapshot;
    private static int frameCounter;

    public static void Update(Session session)
    {
        if (frameCounter++ % UpdateInterval != 0)
            return;

        // Both the client and the server session update every frame and both carry the
        // tracker with the same planets; a session without one changes nothing.
        var tracker = session.TryGet<PlanetTrackerSessionComponent>();
        if (tracker == null)
            return;

        var entries = new List<Entry>();
        foreach (var planet in tracker.Planets)
        {
            var transform = planet.Transform;
            entries.Add(new Entry
            {
                Position = transform.Position,
                Up = Vector3.Normalize(Vector3.Transform(Vector3.Up, transform.Orientation)),
            });
        }

        snapshot = entries.ToArray();
    }

    public static void Clear()
    {
        snapshot = null;
    }

    /// <summary>
    /// Rotation axis (local up) of the planet at the given position, world Y when unknown.
    /// Safe to call from the render thread.
    /// </summary>
    public static Vector3 FindUp(Vector3D position)
    {
        var entries = snapshot;
        if (entries == null)
            return Vector3.Up;

        Entry best = null;
        double bestDistance = MatchDistance;
        foreach (var entry in entries)
        {
            double distance = (entry.Position - position).Length();
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = entry;
            }
        }

        return best?.Up ?? Vector3.Up;
    }
}
