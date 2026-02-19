#include "wasm_module.h"
#include "var_watcher.h"
#include <SimConnect.h>
#include <chrono>
#include <unordered_map>
#include <MSFS/MSFS.h>
#include <MSFS/MSFS_CommBus.h>
#include <MSFS/MSFS_WindowsTypes.h>
#include <MSFS/Legacy/gauges.h>

namespace
{
constexpr auto k_version = "1.1-dev";

enum : DWORD // NOLINT(performance-enum-size)
{
    def_state        = 0xF000,
    def_clock        = 0xF001,
    def_ready        = 0xF101,
    def_control      = 0xF103,
    def_comm_bus_out = 0xF501,
    def_comm_bus_in  = 0xF502,
    def_watch        = 0xF503,
    def_unwatch      = 0xF504,
    def_variable     = 0xF505,
    def_set          = 0xF506,
    def_physics      = 0xF901,
    def_surfaces     = 0xF902
};

struct freeze_state
{
    int32_t lat_lon_freeze;
    int32_t alt_freeze;
    int32_t att_freeze;
    int32_t sim_disabled;
    int32_t fsc_control;
};

static_assert(sizeof(freeze_state) == 20);

HANDLE                                h_sim = 0;
std::chrono::steady_clock::time_point g_start;
double                                g_last_seen           = 0;
volatile bool                         has_standalone_update = false;
bool                                  frz_by_me             = false;
freeze_state                          frz;
var_watcher                           watcher(1e-6);

stream_buffer<fsc::protocol::physics>  phys_buf;
stream_buffer<fsc::protocol::surfaces> ctrl_buf;

std::unordered_map<uint64_t, time_shift> t_shifts;

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

void apply_control(const fsc::protocol::surfaces& c)
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

void apply_control(const fsc::protocol::control control)
{
    if (frz.sim_disabled)
        return;

    if (frz.fsc_control != control.state)
    {
        char cmd[256];
        (void)snprintf(cmd, sizeof(cmd), "%d (>L:FSC_CONTROL)", control.state);
        execute_calculator_code(cmd, nullptr, nullptr, nullptr);
    }

    const bool sim_frozen = frz.alt_freeze || frz.att_freeze || frz.lat_lon_freeze;

    if (control.state == 2 && !sim_frozen)
    {
        execute_calculator_code("1 (>K:FREEZE_LATITUDE_LONGITUDE_SET) "
                                "1 (>K:FREEZE_ALTITUDE_SET) "
                                "1 (>K:FREEZE_ATTITUDE_SET)",
                                nullptr, nullptr, nullptr);
        frz_by_me = true;
        (void)fprintf(stdout, "Freeze engaged");
    }
    else if (control.state == 1 && sim_frozen)
    {
        execute_calculator_code("0 (>K:FREEZE_LATITUDE_LONGITUDE_SET) "
                                "0 (>K:FREEZE_ALTITUDE_SET) "
                                "0 (>K:FREEZE_ATTITUDE_SET)",
                                nullptr, nullptr, nullptr);
        frz_by_me = false;
        (void)fprintf(stdout, "Freeze released");
    }
}

void interpolate(const double now)
{
    if (!frz_by_me)
        return;

    const double render_t = now - k_delay_sec;

    // Physics
    fsc::protocol::physics p{};
    if (phys_buf.interpolate(render_t, p, fsc::interp::physics))
        apply_physics(p);

    // Control
    fsc::protocol::surfaces c{};
    if (ctrl_buf.interpolate(render_t, c, fsc::interp::control))
        apply_control(c);
}

void receive_gauge_msg(const char* json, unsigned int size, void* /*ctx*/)
{
    // const size_t len = std::strlen(json);
    if (size > sizeof(fsc::protocol::str_msg::msg))
        return;

    fsc::protocol::str_msg msg{};
    std::memcpy(msg.msg, json, size);

    (void)SimConnect_SetClientData(h_sim, def_comm_bus_out, def_comm_bus_out, SIMCONNECT_CLIENT_DATA_SET_FLAG_DEFAULT, 0, sizeof(fsc::protocol::str_msg), &msg);
    (void)fprintf(stdout, "Send %s", json);
}

void var_update(const char* name, const double value, void* /*user*/)
{
    (void)fprintf(stdout, "%s -> %f", name, value);
    fsc::protocol::var_set msg = {};
    strncpy(msg.name, name, sizeof(msg.name) - 1);
    msg.value = value;
    (void)SimConnect_SetClientData(h_sim, def_variable, def_variable, SIMCONNECT_CLIENT_DATA_SET_FLAG_DEFAULT, 0, sizeof(fsc::protocol::var_set), &msg);
}

void CALLBACK dispatch(SIMCONNECT_RECV* p_data, DWORD /*cb_data*/, void* /*p_context*/)
{
    if (!p_data)
        return;

    const auto tp  = std::chrono::steady_clock::now();
    const auto now = std::chrono::duration<double>(tp - g_start).count();

    if (p_data->dwID == SIMCONNECT_RECV_ID_SIMOBJECT_DATA)
    {
        const auto* d = reinterpret_cast<SIMCONNECT_RECV_SIMOBJECT_DATA*>(p_data);
        if (d->dwRequestID == def_state)
        {
            const auto* freeze = reinterpret_cast<const freeze_state*>(&d->dwData);
            frz                = *freeze;
        }
    }

    if (p_data->dwID == SIMCONNECT_RECV_ID_CLIENT_DATA)
    {
        const auto* cd = reinterpret_cast<SIMCONNECT_RECV_CLIENT_DATA*>(p_data);

        // If MSFS2024 Update_StandAlone is active, we don't want double-ticking.
        // BUT we still keep SimConnect alive and continue handling messages above.
        // MSFS2020 fallback tick: we use our clock packets as "per-frame" pulse
        if (!has_standalone_update && cd->dwRequestID == def_clock)
        {
            interpolate(now);
            watcher.poll(&var_update, nullptr);
            if (frz.fsc_control != 0 && now - g_last_seen > 1)
            {
                execute_calculator_code("0 (>L:FSC_CONTROL) "
                                        "0 (>K:FREEZE_LATITUDE_LONGITUDE_SET) "
                                        "0 (>K:FREEZE_ALTITUDE_SET) "
                                        "0 (>K:FREEZE_ATTITUDE_SET)",
                                        nullptr, nullptr, nullptr);
                watcher.clear();
            }
        }

        if (cd->dwRequestID == def_control)
        {
            const auto* ctrl = reinterpret_cast<const fsc::protocol::control*>(&cd->dwData);
            apply_control(*ctrl);
            g_last_seen = now;
        }

        if (cd->dwRequestID == def_physics)
        {
            const auto*  physics = reinterpret_cast<const fsc::protocol::physics*>(&cd->dwData);
            const double t_local = t_shifts[physics->session_id].to_local_sec(now, physics->time_ms);
            phys_buf.push(t_local, *physics);
        }

        if (cd->dwRequestID == def_surfaces)
        {
            const auto*  ctrl    = reinterpret_cast<const fsc::protocol::surfaces*>(&cd->dwData);
            const double t_local = t_shifts[ctrl->session_id].to_local_sec(now, ctrl->time_ms);
            ctrl_buf.push(t_local, *ctrl);
        }

        if (cd->dwRequestID == def_comm_bus_in)
        {
            const auto* msg = reinterpret_cast<const fsc::protocol::str_msg*>(&cd->dwData);
            (void)fprintf(stdout, "Receive %s", msg->msg);
            fsCommBusCall("FSC_CLIENT_EVENT", msg->msg, sizeof(msg->msg), FsCommBusBroadcast_JS);
        }

        if (cd->dwRequestID == def_watch)
        {
            const auto* msg = reinterpret_cast<const fsc::protocol::var_set*>(&cd->dwData);
            if (watcher.watch(msg->name, msg->units))
                (void)fprintf(stdout, "Watch %s, %s", msg->name, msg->units);
        }

        if (cd->dwRequestID == def_unwatch)
        {
            const auto* msg = reinterpret_cast<const fsc::protocol::var_set*>(&cd->dwData);
            watcher.unwatch(msg->name);
            (void)fprintf(stdout, "Unwatch %s", msg->name);
        }

        if (cd->dwRequestID == def_set)
        {
            const auto* msg = reinterpret_cast<const fsc::protocol::var_set*>(&cd->dwData);
            char        cmd[128];
            (void)snprintf(cmd, sizeof(cmd), "%.15g (>%s)", msg->value, msg->name);
            execute_calculator_code(cmd, nullptr, nullptr, nullptr);
            (void)fprintf(stdout, cmd);
        }
    }
}
} // namespace

extern "C" MSFS_CALLBACK void module_init(void)
{
    g_start = std::chrono::steady_clock::now();

    if (FAILED(SimConnect_Open(&h_sim, "FsCopilot Bridge", nullptr, 0, 0, 0)))
        return;

    // state
    (void)SimConnect_AddToDataDefinition(h_sim, def_state, "IS LATITUDE LONGITUDE FREEZE ON", "Bool", SIMCONNECT_DATATYPE_INT32);
    (void)SimConnect_AddToDataDefinition(h_sim, def_state, "IS ALTITUDE FREEZE ON", "Bool", SIMCONNECT_DATATYPE_INT32);
    (void)SimConnect_AddToDataDefinition(h_sim, def_state, "IS ATTITUDE FREEZE ON", "Bool", SIMCONNECT_DATATYPE_INT32);
    (void)SimConnect_AddToDataDefinition(h_sim, def_state, "SIM DISABLED", "Bool", SIMCONNECT_DATATYPE_INT32);
    (void)SimConnect_AddToDataDefinition(h_sim, def_state, "L:FSC_CONTROL", "Number", SIMCONNECT_DATATYPE_INT32);
    (void)SimConnect_RequestDataOnSimObject(h_sim, def_state, def_state, SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD_SIM_FRAME, SIMCONNECT_DATA_REQUEST_FLAG_CHANGED, 0, 0, 0);

    // clock. This is required to drive interpolation in MSFS 2020, where Update_StandAlone is not available.
    (void)SimConnect_MapClientDataNameToID(h_sim, "FSC_CLOCK", def_clock);
    (void)SimConnect_CreateClientData(h_sim, def_clock, 4, 0);
    (void)SimConnect_AddToClientDataDefinition(h_sim, def_clock, 0, 4, 0.0f, -1);
    (void)SimConnect_RequestClientData(h_sim, def_clock, def_clock, def_clock, SIMCONNECT_CLIENT_DATA_PERIOD_VISUAL_FRAME, 0, 0, 0, 0);

    // ready
    (void)SimConnect_MapClientDataNameToID(h_sim, "FSC_READY", def_ready);
    (void)SimConnect_CreateClientData(h_sim, def_ready, sizeof(fsc::protocol::str_msg), 0);
    (void)SimConnect_AddToClientDataDefinition(h_sim, def_ready, 0, sizeof(fsc::protocol::str_msg), 0, 0);

    // control
    (void)SimConnect_MapClientDataNameToID(h_sim, "FSC_CONTROL", def_control);
    (void)SimConnect_CreateClientData(h_sim, def_control, sizeof(fsc::protocol::control), 0);
    (void)SimConnect_AddToClientDataDefinition(h_sim, def_control, 0, sizeof(fsc::protocol::control), 0, 0);
    (void)SimConnect_RequestClientData(h_sim, def_control, def_control, def_control, SIMCONNECT_CLIENT_DATA_PERIOD_ON_SET, 0, 0, 0, 0);

    // physics
    (void)SimConnect_MapClientDataNameToID(h_sim, "FSC_PHYSICS", def_physics);
    (void)SimConnect_CreateClientData(h_sim, def_physics, sizeof(fsc::protocol::physics), 0);
    (void)SimConnect_AddToClientDataDefinition(h_sim, def_physics, 0, sizeof(fsc::protocol::physics), 0.0f, -1);
    (void)SimConnect_RequestClientData(h_sim, def_physics, def_physics, def_physics, SIMCONNECT_CLIENT_DATA_PERIOD_ON_SET, 0, 0, 0, 0);

    // surfaces
    (void)SimConnect_MapClientDataNameToID(h_sim, "FSC_SURFACES", def_surfaces);
    (void)SimConnect_CreateClientData(h_sim, def_surfaces, sizeof(fsc::protocol::surfaces), 0);
    (void)SimConnect_AddToClientDataDefinition(h_sim, def_surfaces, 0, sizeof(fsc::protocol::surfaces), 0.0f, -1);
    (void)SimConnect_RequestClientData(h_sim, def_surfaces, def_surfaces, def_surfaces, SIMCONNECT_CLIENT_DATA_PERIOD_ON_SET, 0, 0, 0, 0);

    // bus
    (void)SimConnect_MapClientDataNameToID(h_sim, "FSC_BUS_OUT", def_comm_bus_out);
    (void)SimConnect_CreateClientData(h_sim, def_comm_bus_out, sizeof(fsc::protocol::str_msg), 0);
    (void)SimConnect_AddToClientDataDefinition(h_sim, def_comm_bus_out, 0, sizeof(fsc::protocol::str_msg), 0, 0);
    (void)SimConnect_MapClientDataNameToID(h_sim, "FSC_BUS_IN", def_comm_bus_in);
    (void)SimConnect_CreateClientData(h_sim, def_comm_bus_in, sizeof(fsc::protocol::str_msg), 0);
    (void)SimConnect_AddToClientDataDefinition(h_sim, def_comm_bus_in, 0, sizeof(fsc::protocol::str_msg), 0, 0);
    (void)SimConnect_RequestClientData(h_sim, def_comm_bus_in, def_comm_bus_in, def_comm_bus_in, SIMCONNECT_CLIENT_DATA_PERIOD_ON_SET, 0, 0, 0, 0);
    fsCommBusRegister("FSC_GAUGE_EVENT", receive_gauge_msg, nullptr);

    // watch
    (void)SimConnect_MapClientDataNameToID(h_sim, "FSC_WATCH", def_watch);
    (void)SimConnect_CreateClientData(h_sim, def_watch, sizeof(fsc::protocol::var_set), 0);
    (void)SimConnect_AddToClientDataDefinition(h_sim, def_watch, 0, sizeof(fsc::protocol::var_set), 0, 0);
    (void)SimConnect_RequestClientData(h_sim, def_watch, def_watch, def_watch, SIMCONNECT_CLIENT_DATA_PERIOD_ON_SET, 0, 0, 0, 0);

    // unwatch
    (void)SimConnect_MapClientDataNameToID(h_sim, "FSC_UNWATCH", def_unwatch);
    (void)SimConnect_CreateClientData(h_sim, def_unwatch, sizeof(fsc::protocol::var_set), 0);
    (void)SimConnect_AddToClientDataDefinition(h_sim, def_unwatch, 0, sizeof(fsc::protocol::var_set), 0, 0);
    (void)SimConnect_RequestClientData(h_sim, def_unwatch, def_unwatch, def_unwatch, SIMCONNECT_CLIENT_DATA_PERIOD_ON_SET, 0, 0, 0, 0);

    // watch demon
    (void)SimConnect_MapClientDataNameToID(h_sim, "FSC_VARIABLE", def_variable);
    (void)SimConnect_CreateClientData(h_sim, def_variable, sizeof(fsc::protocol::var_set), 0);
    (void)SimConnect_AddToClientDataDefinition(h_sim, def_variable, 0, sizeof(fsc::protocol::var_set), 0, 0);

    // set
    (void)SimConnect_MapClientDataNameToID(h_sim, "FSC_SET", def_set);
    (void)SimConnect_CreateClientData(h_sim, def_set, sizeof(fsc::protocol::var_set), 0);
    (void)SimConnect_AddToClientDataDefinition(h_sim, def_set, 0, sizeof(fsc::protocol::var_set), 0, 0);
    (void)SimConnect_RequestClientData(h_sim, def_set, def_set, def_set, SIMCONNECT_CLIENT_DATA_PERIOD_ON_SET, 0, 0, 0, 0);

    (void)SimConnect_CallDispatch(h_sim, dispatch, nullptr);

    fsc::protocol::str_msg msg;
    strncpy(msg.msg, k_version, sizeof(msg.msg) - 1);
    (void)SimConnect_SetClientData(h_sim, def_ready, def_ready, SIMCONNECT_CLIENT_DATA_SET_FLAG_DEFAULT, 0, sizeof(msg), &msg);

    (void)fprintf(stdout, "Initialized");
}

extern "C" MSFS_CALLBACK void module_deinit(void)
{
    if (h_sim != 0)
        (void)SimConnect_Close(h_sim);
    fsCommBusUnregisterOneEvent("FSC_GAUGE_EVENT", receive_gauge_msg, nullptr);
    h_sim = 0;
    (void)fprintf(stdout, "Deinitialized");
}

// MSFS2024 standalone update hook
// ReSharper disable once CppInconsistentNaming
extern "C" MSFS_CALLBACK void Update_StandAlone(float d_time)
{
    const auto tp  = std::chrono::steady_clock::now();
    const auto now = std::chrono::duration<double>(tp - g_start).count();
    // now += static_cast<double>(d_time);
    has_standalone_update = true;
    interpolate(now);
    watcher.poll(&var_update, nullptr);
    if (frz.fsc_control != 0 && now - g_last_seen > 1)
    {
        execute_calculator_code("0 (>L:FSC_CONTROL) "
                                "0 (>K:FREEZE_LATITUDE_LONGITUDE_SET) "
                                "0 (>K:FREEZE_ALTITUDE_SET) "
                                "0 (>K:FREEZE_ATTITUDE_SET)",
                                nullptr, nullptr, nullptr);
        watcher.clear();
    }
}
