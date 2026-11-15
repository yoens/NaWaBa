// Shared clock for all stage 4 traps. The stage controller advances it, so every trap
// freezes together during a handoff / intro and speeds up with the difficulty.
public static class TrapClock
{
    public static float Time { get; set; }
    public static float Delta { get; set; }   // seconds advanced this frame (0 while paused)
}
