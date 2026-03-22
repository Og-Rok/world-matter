using Godot;

/// <summary>
/// Thread-safe terrain height sampling for background mesh baking (no Godot objects).
/// Approximates layered FBM; may differ slightly from <see cref="FastNoiseLite"/> on the main thread.
/// </summary>
public struct TerrainNoiseSnapshot
{
    public float amplitude;
    public int seed;
    public float frequency;
    public int fractal_octaves;
    public float fractal_gain;
    public float fractal_lacunarity;
}

public static class WorldTerrainMath
{
    public static float getTerrainDisplacement(
        Vector3 position_on_sphere,
        TerrainNoiseSnapshot[] layers,
        float terrain_height_scale)
    {
        if (layers == null || layers.Length == 0)
        {
            return 0.0f;
        }

        Vector3 unit = position_on_sphere.Normalized();
        float total = 0.0f;
        for (int i = 0; i < layers.Length; i++)
        {
            TerrainNoiseSnapshot layer = layers[i];
            if (layer.amplitude == 0.0f)
            {
                continue;
            }

            total += layer.amplitude * sampleLayerFbm(in layer, unit);
        }

        return total * terrain_height_scale;
    }

    private static float sampleLayerFbm(in TerrainNoiseSnapshot layer, Vector3 unit)
    {
        int octaves = Mathf.Max(1, layer.fractal_octaves);
        float gain = layer.fractal_gain <= 0.0f ? 0.5f : layer.fractal_gain;
        float lac = layer.fractal_lacunarity <= 0.0f ? 2.0f : layer.fractal_lacunarity;
        float freq = layer.frequency;

        float sum = 0.0f;
        float amp = 1.0f;
        Vector3 p = unit;
        for (int o = 0; o < octaves; o++)
        {
            sum += amp * valueNoise3(p * freq, layer.seed + o * 9176);
            freq *= lac;
            amp *= gain;
        }

        return sum;
    }

    private static float valueNoise3(Vector3 p, int seed)
    {
        Vector3 i = new Vector3(
            Mathf.Floor(p.X),
            Mathf.Floor(p.Y),
            Mathf.Floor(p.Z));
        Vector3 f = p - i;
        f = new Vector3(
            f.X * f.X * (3.0f - 2.0f * f.X),
            f.Y * f.Y * (3.0f - 2.0f * f.Y),
            f.Z * f.Z * (3.0f - 2.0f * f.Z));

        float n000 = hash3(i.X, i.Y, i.Z, seed);
        float n100 = hash3(i.X + 1.0f, i.Y, i.Z, seed);
        float n010 = hash3(i.X, i.Y + 1.0f, i.Z, seed);
        float n110 = hash3(i.X + 1.0f, i.Y + 1.0f, i.Z, seed);
        float n001 = hash3(i.X, i.Y, i.Z + 1.0f, seed);
        float n101 = hash3(i.X + 1.0f, i.Y, i.Z + 1.0f, seed);
        float n011 = hash3(i.X, i.Y + 1.0f, i.Z + 1.0f, seed);
        float n111 = hash3(i.X + 1.0f, i.Y + 1.0f, i.Z + 1.0f, seed);

        float nx00 = Mathf.Lerp(n000, n100, f.X);
        float nx10 = Mathf.Lerp(n010, n110, f.X);
        float nx01 = Mathf.Lerp(n001, n101, f.X);
        float nx11 = Mathf.Lerp(n011, n111, f.X);
        float nxy0 = Mathf.Lerp(nx00, nx10, f.Y);
        float nxy1 = Mathf.Lerp(nx01, nx11, f.Y);
        return Mathf.Lerp(nxy0, nxy1, f.Z);
    }

    private static float hash3(float x, float y, float z, int seed)
    {
        int ix = (int)Mathf.Floor(x);
        int iy = (int)Mathf.Floor(y);
        int iz = (int)Mathf.Floor(z);
        unchecked
        {
            uint h = (uint)seed;
            h ^= (uint)ix * 374761393U;
            h ^= (uint)iy * 668265263U;
            h ^= (uint)iz * 2147483647U;
            h = h * 2246822519U ^ (h >> 13);
            h *= 3266489917U;
            int signed = (int)(h & 0x7fffffffU);
            return signed / 2147483647.0f * 2.0f - 1.0f;
        }
    }
}
