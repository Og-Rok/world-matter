using Godot;

[GlobalClass]
[Tool]
public partial class WorldPatchNoise : Resource
{
    [Export]
    public FastNoiseLite noise;

    [Export]
    public float amplitude = 1.0f;

    /// <summary>
    /// Sample 3D noise on the unit sphere (same direction => same value at any subdivision).
    /// </summary>
    public float sampleNoise(Vector3 unit_direction)
    {
        ensureNoise();
        return amplitude * noise.GetNoise3D(unit_direction.X, unit_direction.Y, unit_direction.Z);
    }

    /// <summary>
    /// Godot sometimes fails to bind the nested FastNoiseLite on load; create a sane default so terrain isn't flat.
    /// </summary>
    private void ensureNoise()
    {
        if (noise != null)
        {
            return;
        }

        noise = new FastNoiseLite();
        noise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        noise.Frequency = 0.05f;
        noise.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        noise.FractalOctaves = 4;
    }
}
