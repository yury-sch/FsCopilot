#include "wasm_module.h"
#include <unordered_map>
#include <MSFS/Legacy/gauges.h>
#include <MSFS/MSFS.h>
#include <MSFS/MSFS_WindowsTypes.h>
#include <SimConnect.h>

namespace
{
HANDLE        h_sim                 = 0;
volatile bool has_standalone_update = false;
volatile bool is_frozen             = false;
double        now                   = 0.0;

stream_buffer<fsc::protocol::physics>  phys_buf;
stream_buffer<fsc::protocol::control>  ctrl_buf;

std::unordered_map<uint64_t, time_shift> t_shifts;

enum : DWORD  // NOLINT(performance-enum-size)
{
    // DEF_CLOCK = 0xC001,
    def_freeze   = 0xF001,
    def_physics  = 0xBEEF,
    def_control  = 0xBEEE
};

struct freeze_state
{
    int32_t lat_lon_freeze;
    int32_t alt_freeze;
    int32_t att_freeze;
    int32_t sim_disabled;
};

static_assert(sizeof(freeze_state) == 16);

void apply_physics(const fsc::protocol::physics& p)
{
    char cmd[1024];
    (void)snprintf(cmd, sizeof(cmd),
                   // Position
                   "%.12f (>A:PLANE LATITUDE, Radians) "
                   "%.12f (>A:PLANE LONGITUDE, Radians) "
                   "%.3f  (>A:PLANE ALTITUDE, Feet) "
                   // Attitude / Heading (Degrees)
                   "%.6f  (>A:PLANE PITCH DEGREES, Degrees) "
                   "%.6f  (>A:PLANE BANK DEGREES, Degrees) "
                   "%.6f  (>A:PLANE HEADING DEGREES GYRO, Degrees) "
                   "%.6f  (>A:PLANE HEADING DEGREES TRUE, Degrees) "
                   // Rates / Forces
                   "%.6f  (>A:VERTICAL SPEED, Feet per second) "
                   "%.6f  (>A:G FORCE, Gforce) "
                   // Velocities (body/world)
                   "%.6f  (>A:VELOCITY BODY Y, Feet per second) "
                   "%.6f  (>A:VELOCITY BODY Z, Feet per second) "
                   "%.6f  (>A:VELOCITY WORLD X, Feet per second) "
                   "%.6f  (>A:VELOCITY WORLD Z, Feet per second)",
                   p.lat, p.lon, p.alt_feet, p.pitch, p.bank, p.hdg_deg_gyro, p.hdg_deg_true, p.vertical_speed, p.g_force, p.v_body_y, p.v_body_z, p.vx, p.vz);

    execute_calculator_code(cmd, nullptr, nullptr, nullptr);
}

void apply_control(const fsc::protocol::control& c)
{
    // correct usage depends on aircraft. we use both
    char cmd[256];
    (void)snprintf(cmd, sizeof(cmd),
                   "%d (>A:AILERON POSITION, Position 16k) "
                   "%d (>A:ELEVATOR POSITION, Position 16k) "
                   "%d (>A:RUDDER POSITION, Position 16k)",
                   c.ail_pos, c.elev_pos, c.rud_pos);

    execute_calculator_code(cmd, nullptr, nullptr, nullptr);

    (void)snprintf(cmd, sizeof(cmd),
                   "%d (>K:AXIS_AILERONS_SET) "
                   "%d (>K:AXIS_ELEVATOR_SET) "
                   "%d (>K:AXIS_RUDDER_SET)",
                   -c.ail_pos, -c.elev_pos, -c.rud_pos);
    execute_calculator_code(cmd, nullptr, nullptr, nullptr);
}

void interpolate()
{
    if (!is_frozen)
        return;

    const double render_t = now - k_delay_sec;

    // Physics
    fsc::protocol::physics p{};
    if (phys_buf.interpolate(render_t, p, fsc::interp::physics))
    {
        apply_physics(p);
    }

    // Control
    fsc::protocol::control c{};
    if (ctrl_buf.interpolate(render_t, c, fsc::interp::control))
    {
        apply_control(c);
    }
}

void CALLBACK dispatch(SIMCONNECT_RECV* p_data, DWORD /*cb_data*/, void* /*p_context*/)
{
    if (!p_data)
        return;

    if (p_data->dwID == SIMCONNECT_RECV_ID_SIMOBJECT_DATA)
    {
        const auto* d = reinterpret_cast<SIMCONNECT_RECV_SIMOBJECT_DATA*>(p_data);
        if (d->dwRequestID == def_freeze)
        {
            const auto* fs = reinterpret_cast<const freeze_state*>(&d->dwData);
            is_frozen    = fs->lat_lon_freeze != 0 && fs->alt_freeze != 0 && fs->att_freeze != 0 && fs->sim_disabled == 0; // not frozen by GSX

            // If MSFS2024 Update_StandAlone is active, we don't want double-ticking.
            // BUT we still keep SimConnect alive and continue handling messages above.
            // MSFS2020 fallback tick: we use our clock packets as "per-frame" pulse
            if (!has_standalone_update)
            {
                // now = chrono_system_clock_now();
                now += 1.0 / 60.0;
                interpolate();
            }
        }
    }

    if (p_data->dwID == SIMCONNECT_RECV_ID_CLIENT_DATA)
    {
        const auto* cd = reinterpret_cast<SIMCONNECT_RECV_CLIENT_DATA*>(p_data);
        if (cd->dwRequestID == def_physics)
        {
            const auto*  physics = reinterpret_cast<const fsc::protocol::physics*>(&cd->dwData);
            const double t_local = t_shifts[physics->session_id].to_local_sec(now, physics->time_ms);
            phys_buf.push(t_local, *physics);
        }

        if (cd->dwRequestID == def_control)
        {
            const auto*  ctrl    = reinterpret_cast<const fsc::protocol::control*>(&cd->dwData);
            const double t_local = t_shifts[ctrl->session_id].to_local_sec(now, ctrl->time_ms);
            ctrl_buf.push(t_local, *ctrl);
        }

        // if (!g_has_standalone_update && cd->dwRequestID == DEF_CLOCK)
        // {
        //     // If MSFS2024 Update_StandAlone is active, we don't want double-ticking.
        //     // BUT we still keep SimConnect alive and continue handling messages above.
        //     // MSFS2020 fallback tick: we use our clock packets as "per-frame" pulse
        //     if (!g_has_standalone_update) interpolate();
        // }
    }
}
} // namespace

extern "C" MSFS_CALLBACK void module_init(void)
{
    if (FAILED(SimConnect_Open(&h_sim, "FsCopilot Interpolation", nullptr, 0, 0, 0)))
        return;

    // // Always set up fallback clock — harmless in 2024, critical in 2020.
    // (void)SimConnect_MapClientDataNameToID(g_h_sim, "FSCP_CLOCK", DEF_CLOCK);
    // (void)SimConnect_CreateClientData(g_h_sim, DEF_CLOCK, 4, 0);
    // (void)SimConnect_AddToClientDataDefinition(g_h_sim, DEF_CLOCK, 0, 4, 0.0f, -1);
    // (void)SimConnect_RequestClientData(g_h_sim, DEF_CLOCK, DEF_CLOCK, DEF_CLOCK,
    // SIMCONNECT_CLIENT_DATA_PERIOD_VISUAL_FRAME, 0, 0, 0, 0);

    // freeze
    (void)SimConnect_AddToDataDefinition(h_sim, def_freeze, "IS LATITUDE LONGITUDE FREEZE ON", "Bool", SIMCONNECT_DATATYPE_INT32);
    (void)SimConnect_AddToDataDefinition(h_sim, def_freeze, "IS ALTITUDE FREEZE ON", "Bool", SIMCONNECT_DATATYPE_INT32);
    (void)SimConnect_AddToDataDefinition(h_sim, def_freeze, "IS ATTITUDE FREEZE ON", "Bool", SIMCONNECT_DATATYPE_INT32);
    (void)SimConnect_AddToDataDefinition(h_sim, def_freeze, "SIM DISABLED", "Bool", SIMCONNECT_DATATYPE_INT32);
    // Просим обновлять каждый визуальный кадр (можно SIM_FRAME тоже)
    (void)SimConnect_RequestDataOnSimObject(h_sim, def_freeze, def_freeze, SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD_VISUAL_FRAME, 0, 0, 0, 0);

    // physics
    (void)SimConnect_MapClientDataNameToID(h_sim, "FSC_Physics", def_physics);
    (void)SimConnect_CreateClientData(h_sim, def_physics, sizeof(fsc::protocol::physics), 0);
    (void)SimConnect_AddToClientDataDefinition(h_sim, def_physics, 0, sizeof(fsc::protocol::physics), 0.0f, -1);
    (void)SimConnect_RequestClientData(h_sim, def_physics, def_physics, def_physics, SIMCONNECT_CLIENT_DATA_PERIOD_ON_SET, 0, 0, 0, 0);

    // control
    (void)SimConnect_MapClientDataNameToID(h_sim, "FSC_Control", def_control);
    (void)SimConnect_CreateClientData(h_sim, def_control, sizeof(fsc::protocol::control), 0);
    (void)SimConnect_AddToClientDataDefinition(h_sim, def_control, 0, sizeof(fsc::protocol::control), 0.0f, -1);
    (void)SimConnect_RequestClientData(h_sim, def_control, def_control, def_control, SIMCONNECT_CLIENT_DATA_PERIOD_ON_SET, 0, 0, 0, 0);

    (void)SimConnect_CallDispatch(h_sim, dispatch, nullptr);

    (void)fprintf(stdout, "%s: Initialized", "[FsCopilot]");
}

extern "C" MSFS_CALLBACK void module_deinit(void)
{
    if (h_sim != 0)
    {
        (void)SimConnect_Close(h_sim);
        h_sim = 0;
    }
}

// MSFS2024 standalone update hook
// ReSharper disable once CppInconsistentNaming
extern "C" MSFS_CALLBACK void Update_StandAlone(float d_time)
{
    now += static_cast<double>(d_time);
    has_standalone_update = true;
    interpolate();
}
