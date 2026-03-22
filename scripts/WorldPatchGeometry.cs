using Godot;

/// <summary>
/// Closest point on triangle (Godot <c>Face3::get_closest_point_to</c> port) for LOD distance tests.
/// </summary>
public static class WorldPatchGeometry
{
    /// <summary>Returns the closest point on triangle ABC to <paramref name="p_point"/> (all in the same space).</summary>
    public static Vector3 closestPointOnTriangleToPoint(Vector3 p_point, Vector3 vertex0, Vector3 vertex1, Vector3 vertex2)
    {
        Vector3 edge0 = vertex1 - vertex0;
        Vector3 edge1 = vertex2 - vertex0;
        Vector3 v0 = vertex0 - p_point;

        float aa = edge0.Dot(edge0);
        float bb = edge0.Dot(edge1);
        float cc = edge1.Dot(edge1);
        float dd = edge0.Dot(v0);
        float ee = edge1.Dot(v0);

        float det = aa * cc - bb * bb;
        float s = bb * ee - cc * dd;
        float t = bb * dd - aa * ee;

        if (Mathf.Abs(det) < 1e-20f)
        {
            float d0 = p_point.DistanceTo(vertex0);
            float d1 = p_point.DistanceTo(vertex1);
            float d2 = p_point.DistanceTo(vertex2);
            if (d0 <= d1 && d0 <= d2)
            {
                return vertex0;
            }

            return d1 <= d2 ? vertex1 : vertex2;
        }

        if (s + t < det)
        {
            if (s < 0.0f)
            {
                if (t < 0.0f)
                {
                    if (dd < 0.0f)
                    {
                        s = Mathf.Clamp(-dd / aa, 0.0f, 1.0f);
                        t = 0.0f;
                    }
                    else
                    {
                        s = 0.0f;
                        t = Mathf.Clamp(-ee / cc, 0.0f, 1.0f);
                    }
                }
                else
                {
                    s = 0.0f;
                    t = Mathf.Clamp(-ee / cc, 0.0f, 1.0f);
                }
            }
            else if (t < 0.0f)
            {
                s = Mathf.Clamp(-dd / aa, 0.0f, 1.0f);
                t = 0.0f;
            }
            else
            {
                float inv_det = 1.0f / det;
                s *= inv_det;
                t *= inv_det;
            }
        }
        else
        {
            if (s < 0.0f)
            {
                float tmp0 = bb + dd;
                float tmp1 = cc + ee;
                if (tmp1 > tmp0)
                {
                    float numer = tmp1 - tmp0;
                    float denom = aa - 2.0f * bb + cc;
                    s = Mathf.Clamp(numer / denom, 0.0f, 1.0f);
                    t = 1.0f - s;
                }
                else
                {
                    t = Mathf.Clamp(-ee / cc, 0.0f, 1.0f);
                    s = 0.0f;
                }
            }
            else if (t < 0.0f)
            {
                if (aa + dd > bb + ee)
                {
                    float numer = cc + ee - bb - dd;
                    float denom = aa - 2.0f * bb + cc;
                    s = Mathf.Clamp(numer / denom, 0.0f, 1.0f);
                    t = 1.0f - s;
                }
                else
                {
                    s = Mathf.Clamp(-dd / aa, 0.0f, 1.0f);
                    t = 0.0f;
                }
            }
            else
            {
                float numer = cc + ee - bb - dd;
                float denom = aa - 2.0f * bb + cc;
                s = Mathf.Clamp(numer / denom, 0.0f, 1.0f);
                t = 1.0f - s;
            }
        }

        return vertex0 + edge0 * s + edge1 * t;
    }

    /// <summary>
    /// Minimum distance from <paramref name="p"/> to the two triangles (p00,p10,p11) and (p00,p11,p01).
    /// </summary>
    public static float distancePointToPatchQuad(Vector3 p, Vector3 p00, Vector3 p10, Vector3 p11, Vector3 p01)
    {
        Vector3 c1 = closestPointOnTriangleToPoint(p, p00, p10, p11);
        Vector3 c2 = closestPointOnTriangleToPoint(p, p00, p11, p01);
        float d1 = p.DistanceTo(c1);
        float d2 = p.DistanceTo(c2);
        return Mathf.Min(d1, d2);
    }
}
