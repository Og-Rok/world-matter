using Godot;

[GlobalClass]
[Tool]
public partial class PlanetTerrainLayer : Resource
{
    [ExportGroup("Noise")]
    [Export] public int seed = 0;
    [Export] public FastNoiseLite.NoiseTypeEnum noise_type = FastNoiseLite.NoiseTypeEnum.Perlin;
    [Export] public float frequency = 1.0f;
    [Export] public float amplitude = 1.0f;

    [ExportGroup("Fractal")]
    [Export] public FastNoiseLite.FractalTypeEnum fractal_type = FastNoiseLite.FractalTypeEnum.Fbm;
    [Export] public int fractal_octaves = 3;
    [Export] public float fractal_lacunarity = 2.0f;
    [Export] public float fractal_gain = 0.5f;

    private FastNoiseLite noise;

    /// <summary>
    /// Returns this layer's contribution: amplitude * noise(x, y, z). Uses an internal FastNoiseLite built from this resource's settings.
    /// </summary>
    public float getNoise3D(float x, float y, float z)
    {
        ensureNoise();
        return amplitude * noise.GetNoise3D(x, y, z);
    }

    private void ensureNoise()
    {
        if (noise != null) return;
        noise = new FastNoiseLite();
        noise.Seed = seed;
        noise.NoiseType = noise_type;
        noise.Frequency = frequency;
        noise.FractalType = fractal_type;
        noise.FractalOctaves = fractal_octaves;
        noise.FractalLacunarity = fractal_lacunarity;
        noise.FractalGain = fractal_gain;
    }
}
