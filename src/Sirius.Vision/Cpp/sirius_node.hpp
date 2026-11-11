// sirius_node.hpp - custom nodes for Sirius Studio Plus in C++.
//
// One header, no library to link, C++14 or later. A node is a class with a
// static declare() saying what it is and a run() doing the work; one line at
// the end of one source file, SIRIUS_NODES(...), turns the classes into a
// library the application loads:
//
//     #include "sirius_node.hpp"
//
//     class Invert : public sirius::Node
//     {
//     public:
//         static void declare(sirius::Declaration& d)
//         {
//             d.name("Invert").key("acme.invert").group("Filtering");
//             d.input_image("image", "Image");
//             d.output_image("result", "Result");
//             d.setting_integer("white", "White", 255).range(0, 65535);
//         }
//
//         void run(sirius::Run& run) override
//         {
//             sirius::Image image = run.image("image");
//             ...
//             run.set_image("result", result);
//         }
//     };
//
//     SIRIUS_NODES(Invert)
//
// The library runs inside the application, so what goes wrong in it is
// caught here, at the boundary, and becomes the node's error:
//
//   - a C++ exception of any type. Throw sirius::error, or anything else
//     derived from std::exception - cv::Exception is - and its what() is
//     the message.
//   - a hardware fault - reading through a null pointer, dividing an
//     integer by zero, running off the stack - with Microsoft's compiler or
//     clang-cl. The application then stops using the library and says so,
//     because its memory can no longer be trusted, and stays up so that
//     work can be saved.
//
// What it cannot catch: a fault on a thread the library started itself,
// abort() or std::terminate(), and memory quietly overwritten. Those end
// the application, as they would any program.
//
// Include OpenCV before this header and sirius::to_mat / sirius::from_mat
// wrap an image as a cv::Mat and back without copying.

#ifndef SIRIUS_NODE_HPP
#define SIRIUS_NODE_HPP

#include "sirius_node.h"

#include <cmath>
#include <cstdio>
#include <cstring>
#include <exception>
#include <initializer_list>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

#if defined(_MSC_VER)
#include <excpt.h>
#include <malloc.h>
#endif

namespace sirius
{

static_assert(sizeof(void*) == 8, "Sirius Studio Plus is 64-bit: build the library for x64.");

/// Something a node could not do, worded for whoever is at the screen.
class error : public std::runtime_error
{
public:
    explicit error(const std::string& message) : std::runtime_error(message) {}
};

namespace detail
{
    inline void copy_text(char* destination, int size, const char* text)
    {
        if (destination == nullptr || size <= 0)
            return;

        std::snprintf(destination, static_cast<size_t>(size), "%s", text == nullptr ? "" : text);
    }

    /// A string as JSON writes one.
    inline std::string quoted(const std::string& text)
    {
        std::string out = "\"";

        for (unsigned char c : text)
        {
            switch (c)
            {
                case '"': out += "\\\""; break;
                case '\\': out += "\\\\"; break;
                case '\n': out += "\\n"; break;
                case '\r': out += "\\r"; break;
                case '\t': out += "\\t"; break;
                default:
                    if (c < 0x20)
                    {
                        char escaped[8];
                        std::snprintf(escaped, sizeof(escaped), "\\u%04x", c);
                        out += escaped;
                    }
                    else
                    {
                        out += static_cast<char>(c);
                    }
            }
        }

        return out + "\"";
    }

    /// A number as JSON writes one; one that is not finite has no JSON.
    inline std::string number(double value)
    {
        if (!std::isfinite(value))
            return "null";

        char text[40];
        std::snprintf(text, sizeof(text), "%.17g", value);
        return text;
    }
}

// ---- declaring a node -------------------------------------------------------

/// An input or an output, as declare() describes it.
class Port
{
public:
    Port(std::string name, std::string label, const char* type, bool input)
        : name_(std::move(name)), label_(std::move(label)), type_(type), input_(input) {}

    /// An input that may be left unwired. Check with Run::has before reading it.
    Port& optional() { required_ = false; return *this; }

    Port& description(const std::string& text) { description_ = text; return *this; }

    std::string json() const
    {
        std::string out = "{\"name\":" + detail::quoted(name_) +
                          ",\"label\":" + detail::quoted(label_.empty() ? name_ : label_) +
                          ",\"type\":\"" + type_ + "\"";

        if (input_)
            out += required_ ? ",\"required\":true" : ",\"required\":false";

        if (!description_.empty())
            out += ",\"description\":" + detail::quoted(description_);

        return out + "}";
    }

private:
    std::string name_, label_, description_;
    const char* type_;
    bool input_;
    bool required_ = true;
};

/// A setting, as declare() describes it: shown in the properties panel and saved in the recipe.
class Setting
{
public:
    Setting(std::string name, std::string label, const char* type, std::string fallback)
        : name_(std::move(name)), label_(std::move(label)), type_(type), default_(std::move(fallback)) {}

    /// The lowest and highest it may be set to.
    Setting& range(double minimum, double maximum) { min_ = minimum; max_ = maximum; return *this; }

    /// How far one click of its arrows moves it.
    Setting& step(double amount) { step_ = amount; return *this; }

    Setting& description(const std::string& text) { description_ = text; return *this; }

    /// The heading it is listed under in the properties panel.
    Setting& group(const std::string& heading) { group_ = heading; return *this; }

    /// Gives the setting an input as well, so another node can drive it.
    Setting& connectable() { connectable_ = true; return *this; }

    /// Calls the node's load() again when the setting changes - for a model file, say.
    Setting& reload() { reload_ = true; return *this; }

    /// A slider that gives each power of ten the same travel.
    Setting& logarithmic() { logarithmic_ = true; return *this; }

    std::string json() const
    {
        std::string out = "{\"name\":" + detail::quoted(name_) +
                          ",\"label\":" + detail::quoted(label_.empty() ? name_ : label_) +
                          ",\"type\":\"" + type_ + "\",\"default\":" + default_;

        if (!description_.empty()) out += ",\"description\":" + detail::quoted(description_);
        if (!group_.empty()) out += ",\"group\":" + detail::quoted(group_);
        if (connectable_) out += ",\"connectable\":true";
        if (reload_) out += ",\"reload\":true";
        if (logarithmic_) out += ",\"logarithmic\":true";
        if (std::isfinite(min_)) out += ",\"min\":" + detail::number(min_);
        if (std::isfinite(max_)) out += ",\"max\":" + detail::number(max_);
        if (std::isfinite(step_)) out += ",\"step\":" + detail::number(step_);
        if (!filter_.empty()) out += ",\"filter\":" + detail::quoted(filter_);

        if (!choices_.empty())
        {
            out += ",\"choices\":[";
            for (size_t i = 0; i < choices_.size(); i++)
                out += (i == 0 ? "" : ",") + detail::quoted(choices_[i]);
            out += "]";
        }

        return out + "}";
    }

private:
    friend class Declaration;

    std::string name_, label_, description_, group_, filter_;
    const char* type_;
    std::string default_;
    std::vector<std::string> choices_;
    double min_ = NAN, max_ = NAN, step_ = NAN;
    bool connectable_ = false, reload_ = false, logarithmic_ = false;
};

/// What a node is: its name in the toolbox, its ports and its settings.
///
/// A name - of a port or a setting - is what run() asks for it by, and what
/// a saved recipe knows it by, so it must not change once recipes use it.
/// The label is what the screen shows, and may change freely.
class Declaration
{
public:
    /// The name in the toolbox.
    Declaration& name(const std::string& text) { name_ = text; return *this; }

    /// What saved recipes find the node by: vendor.name, such as "acme.dark-spots". Never change it.
    Declaration& key(const std::string& text) { key_ = text; return *this; }

    /// The toolbox group: "Filtering", "Blob", "Analysis", ... or "Custom", the default.
    Declaration& group(const std::string& text) { group_ = text; return *this; }

    Declaration& description(const std::string& text) { description_ = text; return *this; }

    Declaration& icon(const std::string& text) { icon_ = text; return *this; }

    Port& input_image(const std::string& name, const std::string& label = "") { return input(name, label, "image"); }
    Port& input_number(const std::string& name, const std::string& label = "") { return input(name, label, "number"); }
    Port& input_integer(const std::string& name, const std::string& label = "") { return input(name, label, "integer"); }
    Port& input_bool(const std::string& name, const std::string& label = "") { return input(name, label, "bool"); }
    Port& input_text(const std::string& name, const std::string& label = "") { return input(name, label, "text"); }
    Port& input_shapes(const std::string& name, const std::string& label = "") { return input(name, label, "shapes"); }

    Port& output_image(const std::string& name, const std::string& label = "") { return output(name, label, "image"); }
    Port& output_number(const std::string& name, const std::string& label = "") { return output(name, label, "number"); }
    Port& output_integer(const std::string& name, const std::string& label = "") { return output(name, label, "integer"); }
    Port& output_bool(const std::string& name, const std::string& label = "") { return output(name, label, "bool"); }
    Port& output_text(const std::string& name, const std::string& label = "") { return output(name, label, "text"); }
    Port& output_shapes(const std::string& name, const std::string& label = "") { return output(name, label, "shapes"); }

    Setting& setting_number(const std::string& name, const std::string& label, double fallback = 0)
    {
        return setting(name, label, "number", detail::number(fallback));
    }

    Setting& setting_integer(const std::string& name, const std::string& label, int fallback = 0)
    {
        return setting(name, label, "integer", std::to_string(fallback));
    }

    Setting& setting_bool(const std::string& name, const std::string& label, bool fallback = false)
    {
        return setting(name, label, "bool", fallback ? "true" : "false");
    }

    Setting& setting_text(const std::string& name, const std::string& label, const std::string& fallback = "")
    {
        return setting(name, label, "text", detail::quoted(fallback));
    }

    /// One of a list. The first is the default unless another is named.
    Setting& setting_choice(
        const std::string& name, const std::string& label, std::initializer_list<const char*> choices,
        const std::string& fallback = "")
    {
        std::vector<std::string> list(choices.begin(), choices.end());
        Setting& made = setting(
            name, label, "choice", detail::quoted(!fallback.empty() ? fallback : list.empty() ? "" : list.front()));
        made.choices_ = std::move(list);
        return made;
    }

    /// A file, chosen with a browse button. The filter is "Models|*.onnx;*.pt".
    Setting& setting_file(
        const std::string& name, const std::string& label, const std::string& filter = "",
        const std::string& fallback = "")
    {
        Setting& made = setting(name, label, "file", detail::quoted(fallback));
        made.filter_ = filter;
        return made;
    }

    const std::string& key() const { return key_; }

    std::string json() const
    {
        std::string out = "{\"name\":" + detail::quoted(name_) + ",\"key\":" + detail::quoted(key_) +
                          ",\"group\":" + detail::quoted(group_);

        if (!description_.empty()) out += ",\"description\":" + detail::quoted(description_);
        if (!icon_.empty()) out += ",\"icon\":" + detail::quoted(icon_);

        out += ",\"inputs\":" + list(inputs_) + ",\"outputs\":" + list(outputs_) + ",\"settings\":" + list(settings_);
        return out + "}";
    }

private:
    Port& input(const std::string& name, const std::string& label, const char* type)
    {
        inputs_.emplace_back(name, label, type, true);
        return inputs_.back();
    }

    Port& output(const std::string& name, const std::string& label, const char* type)
    {
        outputs_.emplace_back(name, label, type, false);
        return outputs_.back();
    }

    Setting& setting(const std::string& name, const std::string& label, const char* type, std::string fallback)
    {
        settings_.emplace_back(name, label, type, std::move(fallback));
        return settings_.back();
    }

    template <class T>
    static std::string list(const std::vector<T>& items)
    {
        std::string out = "[";
        for (size_t i = 0; i < items.size(); i++)
            out += (i == 0 ? "" : ",") + items[i].json();
        return out + "]";
    }

    std::string name_, key_, group_ = "Custom", description_, icon_;
    std::vector<Port> inputs_, outputs_;
    std::vector<Setting> settings_;
};

// ---- images -----------------------------------------------------------------

/// An image: a sirius_image with a few conveniences. It owns nothing - it
/// points at pixels that belong to the application, or to the node.
struct Image : sirius_image
{
    Image() : sirius_image() {}

    /// Pixels the node owns, described for set_image.
    Image(void* pixels, int width_, int height_, int channels_, int type, int64_t stride_ = 0,
          int layout_ = SIRIUS_LAYOUT_UNKNOWN, int bits_ = 0)
        : sirius_image()
    {
        width = width_;
        height = height_;
        channels = channels_;
        this->pixels = type;
        layout = layout_;
        bits = bits_;
        data = pixels;
        stride = stride_ != 0 ? stride_ : static_cast<int64_t>(width_) * channels_ * sample_size(type);
    }

    /// True for an input with nothing wired to it.
    bool empty() const { return data == nullptr; }

    /// Bytes in one sample of a type.
    static int sample_size(int type)
    {
        switch (type)
        {
            case SIRIUS_U8: case SIRIUS_S8: return 1;
            case SIRIUS_U16: case SIRIUS_S16: return 2;
            case SIRIUS_S32: case SIRIUS_F32: return 4;
            case SIRIUS_F64: return 8;
            default: return 0;
        }
    }

    /// The largest value a pixel holds: 4095 for a 12-bit frame, 1 for floating point.
    double max_value() const
    {
        int used = bits != 0 ? bits : (pixels == SIRIUS_F32 || pixels == SIRIUS_F64) ? 0 : 8 * sample_size(pixels);
        return used == 0 ? 1.0 : std::pow(2.0, used) - 1.0;
    }

    /// Row y, as samples of T: uint8_t for SIRIUS_U8, uint16_t for SIRIUS_U16, float for SIRIUS_F32, ...
    template <class T>
    const T* row(int y) const
    {
        return reinterpret_cast<const T*>(static_cast<const unsigned char*>(data) + y * stride);
    }
};

// ---- shapes -----------------------------------------------------------------

/// A shape, in the coordinates of the image it was found in.
class Shape
{
public:
    static Shape point(double x, double y) { return Shape(SIRIUS_POINT, x, y); }
    static Shape line(double x1, double y1, double x2, double y2) { return Shape(SIRIUS_LINE, x1, y1, x2, y2); }

    /// x, y is the top left corner.
    static Shape rect(double x, double y, double width, double height) { return Shape(SIRIUS_RECT, x, y, width, height); }

    /// A turned rectangle, about its centre; the angle in degrees.
    static Shape box(double cx, double cy, double width, double height, double angle)
    {
        return Shape(SIRIUS_BOX, cx, cy, width, height, angle);
    }

    static Shape circle(double cx, double cy, double radius) { return Shape(SIRIUS_CIRCLE, cx, cy, radius); }

    static Shape ellipse(double cx, double cy, double major, double minor, double angle)
    {
        return Shape(SIRIUS_ELLIPSE, cx, cy, major, minor, angle);
    }

    /// x0, y0, x1, y1, ... - closed, or an open line.
    static Shape polygon(std::vector<double> xy, bool closed = true)
    {
        Shape shape(SIRIUS_POLYGON);
        shape.points = std::move(xy);
        shape.closed = closed;
        return shape;
    }

    /// Written beside the shape in the viewer.
    Shape& with_label(const std::string& text) { label = text; return *this; }

    /// 0xAARRGGBB.
    Shape& with_colour(uint32_t argb) { colour = argb; return *this; }

    /// A measurement the shape carries: listed with it, and what Filter Shapes selects by.
    Shape& with_metric(const std::string& name, double value)
    {
        metrics.emplace_back(name, value);
        return *this;
    }

    int kind = SIRIUS_POINT;
    double v[5] = { 0, 0, 0, 0, 0 };
    std::vector<double> points;
    bool closed = true;
    std::string label;
    uint32_t colour = 0;
    std::vector<std::pair<std::string, double>> metrics;

private:
    explicit Shape(int kind_, double a = 0, double b = 0, double c = 0, double d = 0, double e = 0) : kind(kind_)
    {
        v[0] = a; v[1] = b; v[2] = c; v[3] = d; v[4] = e;
    }

    friend class Run;
};

// ---- a run --------------------------------------------------------------------

/// What a node is given while it runs: its settings and inputs, by the
/// names it declared, and somewhere to put its outputs.
///
/// Asking for a name the node did not declare, or as the wrong type, throws
/// a sirius::error that says so - which, uncaught, is the node's error.
class Run
{
public:
    explicit Run(const sirius_host* host) : host_(host) {}

    /// True for a setting, or an input with something wired to it.
    bool has(const char* name) const { return check(host_->has(host_->context, name)) == 1; }

    /// A number: a setting, or an input. An input with nothing wired throws.
    double number(const char* name) const
    {
        double value = 0;
        wired(check(host_->get_number(host_->context, name, &value)), name);
        return value;
    }

    /// A number, or the fallback for an input with nothing wired.
    double number(const char* name, double fallback) const
    {
        double value = 0;
        return check(host_->get_number(host_->context, name, &value)) == 1 ? value : fallback;
    }

    int integer(const char* name) const
    {
        int64_t value = 0;
        wired(check(host_->get_integer(host_->context, name, &value)), name);
        return static_cast<int>(value);
    }

    int integer(const char* name, int fallback) const
    {
        int64_t value = 0;
        return check(host_->get_integer(host_->context, name, &value)) == 1 ? static_cast<int>(value) : fallback;
    }

    bool boolean(const char* name) const
    {
        int32_t value = 0;
        wired(check(host_->get_bool(host_->context, name, &value)), name);
        return value != 0;
    }

    bool boolean(const char* name, bool fallback) const
    {
        int32_t value = 0;
        return check(host_->get_bool(host_->context, name, &value)) == 1 ? value != 0 : fallback;
    }

    /// A text, a choice or a file setting, or a text input. Empty for an input with nothing wired.
    std::string text(const char* name) const
    {
        const char* value = nullptr;
        return check(host_->get_text(host_->context, name, &value)) == 1 && value != nullptr ? value : "";
    }

    /// An image input. Read-only, and valid only until run() returns;
    /// empty() when nothing is wired.
    Image image(const char* name) const
    {
        Image image;
        if (check(host_->get_image(host_->context, name, &image)) != 1)
            image.data = nullptr;
        return image;
    }

    /// A shapes input; none when nothing is wired.
    std::vector<Shape> shapes(const char* name) const
    {
        std::vector<Shape> result;
        int32_t count = 0;

        if (check(host_->get_shape_count(host_->context, name, &count)) != 1)
            return result;

        result.reserve(static_cast<size_t>(count));

        for (int32_t i = 0; i < count; i++)
        {
            sirius_shape raw = sirius_shape();
            check(host_->get_shape(host_->context, name, i, &raw));

            Shape shape(raw.kind, raw.v[0], raw.v[1], raw.v[2], raw.v[3], raw.v[4]);
            shape.closed = raw.closed != 0;
            shape.colour = raw.colour;

            if (raw.label != nullptr)
                shape.label = raw.label;

            if (raw.points != nullptr && raw.point_count > 0)
                shape.points.assign(raw.points, raw.points + (2 * static_cast<size_t>(raw.point_count)));

            for (int32_t m = 0; m < raw.metric_count; m++)
                shape.metrics.emplace_back(raw.metrics[m].name, raw.metrics[m].value);

            result.push_back(std::move(shape));
        }

        return result;
    }

    void set_number(const char* name, double value) { check(host_->set_number(host_->context, name, value)); }
    void set_integer(const char* name, long long value) { check(host_->set_integer(host_->context, name, value)); }
    void set_bool(const char* name, bool value) { check(host_->set_bool(host_->context, name, value ? 1 : 0)); }
    void set_text(const char* name, const std::string& value) { check(host_->set_text(host_->context, name, value.c_str())); }

    /// An image output. The pixels are copied before this returns.
    void set_image(const char* name, const sirius_image& image) { check(host_->set_image(host_->context, name, &image)); }

    /// Adds one shape to a shapes output.
    void add_shape(const char* name, const Shape& shape)
    {
        std::vector<sirius_metric> metrics;
        metrics.reserve(shape.metrics.size());

        for (const auto& metric : shape.metrics)
            metrics.push_back(sirius_metric{ metric.first.c_str(), metric.second });

        sirius_shape raw = sirius_shape();
        raw.kind = shape.kind;
        raw.closed = shape.closed ? 1 : 0;
        std::memcpy(raw.v, shape.v, sizeof(raw.v));
        raw.points = shape.points.empty() ? nullptr : shape.points.data();
        raw.point_count = static_cast<int32_t>(shape.points.size() / 2);
        raw.metric_count = static_cast<int32_t>(metrics.size());
        raw.metrics = metrics.empty() ? nullptr : metrics.data();
        raw.label = shape.label.empty() ? nullptr : shape.label.c_str();
        raw.colour = shape.colour;

        check(host_->add_shape(host_->context, name, &raw));
    }

    void set_shapes(const char* name, const std::vector<Shape>& shapes)
    {
        for (const Shape& shape : shapes)
            add_shape(name, shape);
    }

    /// Shown on the node in amber; the run still succeeds.
    void warn(const std::string& message) { host_->warn(host_->context, message.c_str()); }

    /// True once the run has been cancelled: stop when convenient and return.
    bool cancelled() const { return host_->cancelled(host_->context) != 0; }

    /// The folder the library's DLL is in, for a model file kept beside it.
    std::string library_folder() const
    {
        const char* folder = host_->library_folder(host_->context);
        return folder == nullptr ? "" : folder;
    }

    const sirius_host* host() const { return host_; }

private:
    int check(int result) const
    {
        if (result < 0)
        {
            const char* why = host_->last_error(host_->context);
            throw error(why == nullptr ? "The application refused the call." : why);
        }

        return result;
    }

    static void wired(int result, const char* name)
    {
        if (result != 1)
            throw error(std::string("Nothing is wired to '") + name + "'.");
    }

    const sirius_host* host_;
};

// ---- a node -------------------------------------------------------------------

/// A node. The application makes one object for each node on the canvas and
/// keeps it for as long as the node is there, so what load() builds - a
/// model, a lookup table - is built once.
///
/// A class also needs:  static void declare(sirius::Declaration&);
class Node
{
public:
    virtual ~Node() {}

    /// Called before the first run, and again when a setting declared with
    /// reload() changes. Only settings can be read here.
    virtual void load(Run&) {}

    /// Called once for each inspection.
    virtual void run(Run&) = 0;
};

#if defined(OPENCV_CORE_HPP) || defined(SIRIUS_WITH_OPENCV)

/// An image as a cv::Mat over the same pixels - no copy. An input's pixels
/// are the application's: do not write to the matrix, and do not keep it
/// after run() returns. clone() it for either.
inline cv::Mat to_mat(const sirius_image& image)
{
    return image.data == nullptr
        ? cv::Mat()
        : cv::Mat(image.height, image.width, CV_MAKETYPE(image.pixels, image.channels), image.data,
                  static_cast<size_t>(image.stride));
}

/// A cv::Mat described for set_image - no copy here; set_image makes the one copy.
inline Image from_mat(const cv::Mat& mat, int layout = SIRIUS_LAYOUT_UNKNOWN, int bits = 0)
{
    return Image(mat.data, mat.cols, mat.rows, mat.channels(), mat.depth(), static_cast<int64_t>(mat.step), layout, bits);
}

#endif

// ---- the boundary ---------------------------------------------------------------

namespace detail
{
    struct Entry
    {
        std::string key;
        Node* (*make)();
    };

    struct Registry
    {
        std::vector<Entry> entries;
        std::string json;
        std::string problem;
    };

    inline Registry& registry()
    {
        static Registry instance;
        return instance;
    }

    template <class T>
    Node* make()
    {
        return new T();
    }

    template <class T>
    void add(Registry& registry)
    {
        Declaration declaration;
        T::declare(declaration);

        registry.entries.push_back(Entry{ declaration.key(), &make<T> });
        registry.json += (registry.json.empty() ? "" : ",") + declaration.json();
    }

    // What a call across the boundary carries, so that every one of them can
    // go through the same two guards.
    struct Call
    {
        int what;
        const char* key;
        void** made;
        Node* node;
        const sirius_host* host;
        char* error;
        int error_size;
    };

    enum { CallCreate, CallDestroy, CallLoad, CallRun };

    /// The first guard: no C++ exception leaves the library.
    inline int call_cpp(void* argument)
    {
        Call* call = static_cast<Call*>(argument);

        try
        {
            switch (call->what)
            {
                case CallCreate:
                {
                    for (const Entry& entry : registry().entries)
                    {
                        if (entry.key == call->key)
                        {
                            *call->made = entry.make();
                            return SIRIUS_OK;
                        }
                    }

                    throw error(std::string("This library has no node with the key '") + call->key + "'.");
                }

                case CallDestroy:
                    delete call->node;
                    return SIRIUS_OK;

                case CallLoad:
                {
                    Run run(call->host);
                    call->node->load(run);
                    return SIRIUS_OK;
                }

                default:
                {
                    Run run(call->host);
                    call->node->run(run);
                    return SIRIUS_OK;
                }
            }
        }
#if defined(SIRIUS_EXTRA_CATCH)
        // For a library whose exceptions are not std::exception's - see the guide.
        SIRIUS_EXTRA_CATCH(call->error, call->error_size)
#endif
        catch (const std::bad_alloc&)
        {
            copy_text(call->error, call->error_size, "It ran out of memory.");
        }
        catch (const std::exception& ex)
        {
            copy_text(call->error, call->error_size, ex.what());
        }
        catch (...)
        {
            copy_text(call->error, call->error_size, "It threw an exception that says nothing about itself.");
        }

        return SIRIUS_FAILED;
    }

#if defined(_MSC_VER)

    struct Fault
    {
        unsigned long code;
        void* at;
        int has_access;
        unsigned long long access;
        void* target;
    };

    // Which exceptions are faults. A C++ exception and a .NET one are not:
    // they are on their way to a handler of their own.
    inline int is_fault(unsigned long code, void* information, Fault* fault)
    {
        if (code == 0xE06D7363UL || code == 0xE0434352UL || (code & 0xC0000000UL) != 0xC0000000UL)
            return 0; // EXCEPTION_CONTINUE_SEARCH

        fault->code = code;
        fault->at = nullptr;
        fault->has_access = 0;

        // EXCEPTION_POINTERS begins with a pointer to an EXCEPTION_RECORD,
        // which on 64-bit Windows is: code, flags (4 bytes each), a pointer
        // to the next record, the address, a count, and the parameters.
        // Read by offset, so this header needs nothing from <windows.h>.
        if (information != nullptr)
        {
            const unsigned char* record = *static_cast<const unsigned char* const*>(information);

            if (record != nullptr)
            {
                unsigned int count = 0;
                std::memcpy(&fault->at, record + 16, sizeof(void*));
                std::memcpy(&count, record + 24, sizeof(count));

                if (code == 0xC0000005UL && count >= 2)
                {
                    fault->has_access = 1;
                    std::memcpy(&fault->access, record + 32, sizeof(fault->access));
                    std::memcpy(&fault->target, record + 40, sizeof(void*));
                }
            }
        }

        return 1; // EXCEPTION_EXECUTE_HANDLER
    }

    /// The second guard: a hardware fault inside the call comes back as
    /// SIRIUS_CRASHED, with where it happened for the application to name.
    inline int guarded(int (*call)(void*), void* argument, char* error, int error_size)
    {
        Fault fault = { 0, nullptr, 0, 0, nullptr };

        __try
        {
            return call(argument);
        }
        __except (is_fault(GetExceptionCode(), GetExceptionInformation(), &fault))
        {
            // Running off the stack leaves its guard page spent; put it back
            // or the next overflow ends the process without a word.
            if (fault.code == 0xC00000FDUL)
                _resetstkoflw();

            if (error != nullptr && error_size > 0)
            {
                if (fault.has_access)
                {
                    std::snprintf(error, static_cast<size_t>(error_size), "fault code=%08lX at=%p access=%llu target=%p",
                                  fault.code, fault.at, fault.access, fault.target);
                }
                else
                {
                    std::snprintf(error, static_cast<size_t>(error_size), "fault code=%08lX at=%p", fault.code, fault.at);
                }
            }

            return SIRIUS_CRASHED;
        }
    }

    inline int guarded_copy(void* destination, const void* source, int64_t bytes)
    {
        __try
        {
            std::memcpy(destination, source, static_cast<size_t>(bytes));
            return SIRIUS_OK;
        }
        __except (GetExceptionCode() == 0xC0000005UL || GetExceptionCode() == 0xC0000006UL ? 1 : 0)
        {
            return SIRIUS_CRASHED;
        }
    }

#else

    // Other compilers have no way to catch a hardware fault in-line.
    inline int guarded(int (*call)(void*), void* argument, char*, int)
    {
        return call(argument);
    }

    inline int guarded_copy(void* destination, const void* source, int64_t bytes)
    {
        std::memcpy(destination, source, static_cast<size_t>(bytes));
        return SIRIUS_OK;
    }

#endif

    inline int describe(const char** json, char* error, int32_t error_size)
    {
        Registry& all = registry();

        if (!all.problem.empty())
        {
            copy_text(error, error_size, all.problem.c_str());
            return SIRIUS_FAILED;
        }

        *json = all.json.c_str();
        return SIRIUS_OK;
    }

    inline int create(const char* key, void** node, char* error, int32_t error_size)
    {
        Call call = { CallCreate, key, node, nullptr, nullptr, error, error_size };
        return guarded(&call_cpp, &call, error, error_size);
    }

    inline void destroy(void* node)
    {
        Call call = { CallDestroy, nullptr, nullptr, static_cast<Node*>(node), nullptr, nullptr, 0 };
        guarded(&call_cpp, &call, nullptr, 0);
    }

    inline int load(void* node, const sirius_host* host, char* error, int32_t error_size)
    {
        Call call = { CallLoad, nullptr, nullptr, static_cast<Node*>(node), host, error, error_size };
        return guarded(&call_cpp, &call, error, error_size);
    }

    inline int run(void* node, const sirius_host* host, char* error, int32_t error_size)
    {
        Call call = { CallRun, nullptr, nullptr, static_cast<Node*>(node), host, error, error_size };
        return guarded(&call_cpp, &call, error, error_size);
    }

    /// Declares every class once, and hands back the table of entry points.
    template <class... Nodes>
    const sirius_library* library()
    {
        static const sirius_library table = {
            SIRIUS_API_VERSION, static_cast<int32_t>(sizeof(sirius_library)),
            &describe, &create, &destroy, &load, &run, &guarded_copy,
        };

        static const bool declared = []
        {
            Registry& all = registry();

            try
            {
                int each[] = { 0, (add<Nodes>(all), 0)... };
                (void)each;
                all.json = "[" + all.json + "]";
            }
            catch (const std::exception& ex)
            {
                all.problem = std::string("A node could not be declared: ") + ex.what();
            }
            catch (...)
            {
                all.problem = "A node could not be declared: its declare() threw an exception.";
            }

            return true;
        }();

        (void)declared;
        return &table;
    }
}

} // namespace sirius

/// Turns the node classes into a library: once, in one source file.
#define SIRIUS_NODES(...)                                                         \
    extern "C" SIRIUS_EXPORT const sirius_library* sirius_node_library(void)      \
    {                                                                             \
        return sirius::detail::library<__VA_ARGS__>();                            \
    }

#endif // SIRIUS_NODE_HPP
