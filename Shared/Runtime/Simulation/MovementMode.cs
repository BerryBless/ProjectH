namespace ProjectH.Shared.Simulation
{
    // Phase 12 D1: the one movement state of a character. MovementSimulation.Step branches on it; the server, client
    // prediction and the snapshot (SnapshotEntity flag bits 1-3) all see the same value. Values are the wire format:
    // never renumber. Sprinting and airborne are not modes: they follow from the input and the ground check.
    public enum MovementMode : byte
    {
        Ground = 0,     // standing, walking, sprinting, jumping and falling
        Crouch = 1,
        Slide = 2,
        Vault = 3,      // a mantle or a hurdle in progress (MoveState.ModeTicks left)
        Freefall = 4,
        Glide = 5,
        Transport = 6,  // riding the drop transport (DropTransport.Ride places the character)
        // Phase 14 D4: knocked down (DBNO). Crawls at MovementTuning.CrawlSpeed with a DownedHeight box; no jump, sprint,
        // posture change, vault or slide. Only the server enters and leaves it (a fatal hit, a revive); the simulation
        // never changes it. The last value the snapshot's 3 mode bits can hold.
        Downed = 7,
    }
}
