// Shared clock for the graveyard ghosts. The stage controller advances it, so every ghost
// freezes together during a handoff / intro and speeds up with the difficulty.
// Ghost positions are computed from this time, so the patrols always stay in step.
public static class GraveClock
{
    public static float Time { get; set; }
    public static float Delta { get; set; }   // seconds advanced this frame (0 while paused)
}
