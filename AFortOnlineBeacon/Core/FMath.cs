namespace AFortOnlineBeacon.Core;

public static class FMath {
    public static int DivideAndRoundUp(int dividend, int divisor) => (dividend + divisor - 1) / divisor;

    public static float FRandRange(float min, float max) => min + (max - min) * Random.Shared.NextSingle();
}