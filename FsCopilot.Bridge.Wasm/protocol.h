#pragma once
#include <cstdint>

namespace fsc::protocol
{
#pragma pack(push, 1)

struct physics
{
    double   lat;
    double   lon;
    double   alt_feet;
    double   pitch;
    double   bank;
    double   hdg_deg_gyro;
    double   hdg_deg_true;
    double   vertical_speed;
    double   g_force;
    double   v_body_y;
    double   v_body_z;
    double   vx;
    double   vz;
    uint64_t session_id;
    uint32_t time_ms;
};

struct control
{
    int32_t  ail_pos;
    int32_t  elev_pos;
    int32_t  rud_pos;
    uint64_t session_id;
    uint32_t time_ms;
};

struct throttle
{
    int32_t   throttle1;
    int32_t   throttle2;
    int32_t   throttle3;
    int32_t   throttle4;
    uint64_t session_id;
    uint32_t time_ms;
};

struct str_msg
{
    char msg[512];
};

struct var_set
{
    char   name[128];
    char   units[64];
    double value;
};

#pragma pack(pop)

static_assert(sizeof(physics) == 116);
static_assert(sizeof(control) == 24);
static_assert(sizeof(throttle) == 28);
static_assert(sizeof(str_msg) == 512);
static_assert(sizeof(var_set) == 200);
} // namespace fsc::protocol