#pragma once
#include <cmath>

namespace fsc::interp
{
inline double lerp(const double a, const double b, const double t)
{
    return a + (b - a) * t;
}

// inline double lerp_angle_rad(double a, double b, double t)
// {
//     constexpr double pi = 3.14159265358979323846;
//     double d = b - a;
//     while (d >  pi) d -= 2.0 * pi;
//     while (d < -pi) d += 2.0 * pi;
//     return a + d * t;
// }
//
// inline double lerp_angle_deg(double a, double b, double t)
// {
//     double d = b - a;
//
//     // Normalize to [-180, 180]
//     while (d >  180.0) d -= 360.0;
//     while (d < -180.0) d += 360.0;
//
//     return a + d * t;
// }

inline double norm360(double a)
{
    a = std::fmod(a, 360.0);
    if (a < 0.0)
        a += 360.0;
    return a;
}

inline double norm180(double a)
{
    a = norm360(a);
    if (a >= 180.0)
        a -= 360.0;
    return a;
}

inline double lerp_360_deg(double a_deg, double b_deg, const double t)
{
    a_deg = norm360(a_deg);
    b_deg = norm360(b_deg);

    double d = b_deg - a_deg;

    if (d > 180.0)
        d -= 360.0;
    if (d < -180.0)
        d += 360.0;

    return norm360(a_deg + d * t);
}

inline double lerp_180_deg(double a_deg, double b_deg, const double t)
{
    a_deg = norm180(a_deg);
    b_deg = norm180(b_deg);

    double d = b_deg - a_deg;

    // Still use shortest path (same rule).
    if (d > 180.0)
        d -= 360.0;
    if (d < -180.0)
        d += 360.0;

    return norm180(a_deg + d * t);
}

inline void physics(const protocol::physics& a, const protocol::physics& b, const double t, protocol::physics& out)
{
    out.lat      = lerp(a.lat, b.lat, t);
    out.lon      = lerp(a.lon, b.lon, t);
    out.alt_feet = lerp(a.alt_feet, b.alt_feet, t);

    out.pitch = lerp_180_deg(a.pitch, b.pitch, t);
    out.bank  = lerp_180_deg(a.bank, b.bank, t);

    out.hdg_deg_gyro = lerp_360_deg(a.hdg_deg_gyro, b.hdg_deg_gyro, t);
    out.hdg_deg_true = lerp_360_deg(a.hdg_deg_true, b.hdg_deg_true, t);

    out.vertical_speed = lerp(a.vertical_speed, b.vertical_speed, t);
    out.g_force        = lerp(a.g_force, b.g_force, t);
    out.v_body_y       = lerp(a.v_body_y, b.v_body_y, t);
    out.v_body_z       = lerp(a.v_body_z, b.v_body_z, t);
    out.vx             = lerp(a.vx, b.vx, t);
    out.vz             = lerp(a.vz, b.vz, t);
}

inline void control(const protocol::control& a, const protocol::control& b, const double t, protocol::control& out)
{
    out.ail_pos  = static_cast<int32_t>(llround(lerp(a.ail_pos, b.ail_pos, t)));
    out.elev_pos = static_cast<int32_t>(llround(lerp(a.elev_pos, b.elev_pos, t)));
    out.rud_pos  = static_cast<int32_t>(llround(lerp(a.rud_pos, b.rud_pos, t)));
}

inline void throttle(const protocol::throttle& a, const protocol::throttle& b, const double t, protocol::throttle& out)
{
    out.throttle1 = static_cast<int32_t>(llround(lerp(a.throttle1, b.throttle1, t)));
    out.throttle2 = static_cast<int32_t>(llround(lerp(a.throttle2, b.throttle2, t)));
    out.throttle3 = static_cast<int32_t>(llround(lerp(a.throttle3, b.throttle3, t)));
    out.throttle4 = static_cast<int32_t>(llround(lerp(a.throttle4, b.throttle4, t)));
}
} // namespace fsc::interp
