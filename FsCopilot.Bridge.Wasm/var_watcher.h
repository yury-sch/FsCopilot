#pragma once

#include <unordered_map>
#include <string>

#include <MSFS/Legacy/gauges.h> // execute_calculator_code

typedef void (*var_update_callback)(
    const char* name,
    double value,
    void* user_data
);

class var_watcher
{
public:
    struct watch_entry
    {
        std::string wrapped_expr; // "(name, units)" or "(name)"
        double      last_value;
        bool        has_last;

        watch_entry() : wrapped_expr(), last_value(0.0), has_last(false) {}
        explicit watch_entry(const std::string& expr)
            : wrapped_expr(expr), last_value(0.0), has_last(false)
        {
        }
    };

    explicit var_watcher(double epsilon = 1e-6)
        : epsilon_(epsilon)
    {
    }

    bool watch(const char* name, const char* units)
    {
        const std::string key(name);

        if (vars_.find(key) != vars_.end())
            return false;

        vars_[key] = watch_entry(wrap_expression(name, units));
        return true;
    }

    void unwatch(const char* name)
    {
        vars_.erase(std::string(name));
    }

    void clear()
    {
        if (vars_.empty()) return;
        vars_.clear();
    }

    void set_epsilon(double epsilon)
    {
        epsilon_ = epsilon;
    }

    void poll(var_update_callback cb, void* user_data)
    {
        if (vars_.empty() || cb == 0)
            return;

        for (std::unordered_map<std::string, watch_entry>::iterator it = vars_.begin();
             it != vars_.end();
             ++it)
        {
            const std::string& name = it->first;
            watch_entry& entry = it->second;

            double new_value = read_calc_double(entry.wrapped_expr.c_str());

            if (!entry.has_last)
            {
                entry.last_value = new_value;
                entry.has_last = true;
                cb(name.c_str(), new_value, user_data);
                continue;
            }

            if (equals(entry.last_value, new_value))
                continue;

            entry.last_value = new_value;
            cb(name.c_str(), new_value, user_data);
        }
    }

private:
    static bool is_empty_cstr(const char* s)
    {
        return (s == 0) || (s[0] == '\0');
    }

    static std::string wrap_expression(const char* name, const char* units)
    {
        std::string wrapped;
        wrapped.push_back('(');
        wrapped.append(name);

        if (!is_empty_cstr(units))
        {
            wrapped.append(", ");
            wrapped.append(units);
        }

        wrapped.push_back(')');
        return wrapped;
    }

    static double read_calc_double(const char* wrapped_expression)
    {
        double result = 0.0;
        execute_calculator_code(wrapped_expression, &result, 0, 0);
        return result;
    }

    static bool is_nan(double x)
    {
        return x != x;
    }

    bool equals(double a, double b) const
    {
        if (is_nan(b))
            return false;

        double d = b - a;
        if (d < 0.0) d = -d;
        return d <= epsilon_;
    }

private:
    std::unordered_map<std::string, watch_entry> vars_;
    double epsilon_;
};