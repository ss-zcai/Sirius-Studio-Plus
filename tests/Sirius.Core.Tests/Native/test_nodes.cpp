// The C++ node library the tests build and load: nodes that exercise every
// part of the interface, and - by the Level setting - go wrong in each of
// the ways a real one can.

// An exception that is not a std::exception, as HALCON's is not - and the
// hook that gives the application its message all the same.
struct VendorError
{
    const char* text;
};

#define SIRIUS_EXTRA_CATCH(error, size)     catch (const VendorError& ex) { copy_text(error, size, (std::string("Vendor: ") + ex.text).c_str()); }

#include "sirius_node.hpp"

#include <cstdint>
#include <stdexcept>
#include <string>
#include <vector>

namespace
{

#if defined(_MSC_VER)
#pragma warning(disable : 4717) // recursive on all control paths - that is the point
#pragma optimize("", off)
#endif

int deeper(int depth)
{
    volatile char filler[4096];
    filler[0] = static_cast<char>(depth);
    return deeper(depth + 1) + filler[0];
}

int divide(int by)
{
    volatile int zero = by;
    return 100 / zero;
}

void write_through(int* pointer)
{
    volatile int* target = pointer;
    *target = 1;
}

#if defined(_MSC_VER)
#pragma optimize("", on)
#endif

} // namespace

class Invert : public sirius::Node
{
public:
    static void declare(sirius::Declaration& d)
    {
        d.name("Cpp Invert").key("cpptest.invert").group("Filtering").description("Inverts.");

        d.input_image("image", "Image");

        d.output_image("result", "Result");
        d.output_shapes("spots", "Spots");
        d.output_integer("loads", "Loads");
        d.output_text("note", "Note");
        d.output_bool("bright", "Bright");
        d.output_number("top", "Top");

        d.setting_integer("level", "Level", 60).range(0, 255);
        d.setting_text("model", "Model", "a").reload();
        d.setting_choice("mode", "Mode", { "fast", "accurate" });
        d.setting_number("gain", "Gain", 1.5).connectable();
        d.setting_file("weights", "Weights", "Models|*.onnx");
    }

    void load(sirius::Run& run) override
    {
        loads_++;
        model_ = run.text("model");

        if (model_ == "missing")
            throw sirius::error("The model 'missing' is not in " + run.library_folder());

        if (run.has("image"))
            throw sirius::error("load() was shown an input.");
    }

    void run(sirius::Run& run) override
    {
        const int level = run.integer("level");
        const sirius::Image image = run.image("image");

        switch (level)
        {
            case 13: throw std::runtime_error("unlucky level");
            case 14: throw 42;
            case 15: throw VendorError{ "the licence dongle is missing" };
            case 21: run.number("no_such_setting"); break;
            case 22: run.set_number("note", 1.0); break;
            case 23: run.integer("gain"); break;
            case 24:
            {
                // An image that says it is bigger than the memory behind it.
                sirius::Image stale(reinterpret_cast<void*>(static_cast<uintptr_t>(0x10)), 64, 64, 1, SIRIUS_U8);
                run.set_image("result", stale);
                break;
            }
            case 25:
            {
                uint8_t pixel = 0;
                sirius::Image narrow(&pixel, 4, 1, 1, SIRIUS_U8, 2);
                run.set_image("result", narrow);
                break;
            }
            case 97: deeper(0); break;
            case 98: run.set_integer("loads", divide(0)); break;
            case 99: write_through(nullptr); break;
            default: break;
        }

        if (level == 7)
            run.warn("seven is low");

        // The result, inverted within the image's own range, in rows of
        // their own - so a stride that is not the row length is honoured.
        const int samples = image.width * image.channels;
        const double top = image.max_value();
        std::vector<uint16_t> wide;
        std::vector<uint8_t> narrow;

        if (image.pixels == SIRIUS_U16)
        {
            wide.resize(static_cast<size_t>(samples) * image.height);
            for (int y = 0; y < image.height; y++)
                for (int x = 0; x < samples; x++)
                    wide[static_cast<size_t>(y) * samples + x] = static_cast<uint16_t>(top - image.row<uint16_t>(y)[x]);

            run.set_image("result", sirius::Image(wide.data(), image.width, image.height, image.channels, SIRIUS_U16, 0, image.layout));
        }
        else if (image.pixels == SIRIUS_U8)
        {
            narrow.resize(static_cast<size_t>(samples) * image.height);
            for (int y = 0; y < image.height; y++)
                for (int x = 0; x < samples; x++)
                    narrow[static_cast<size_t>(y) * samples + x] = static_cast<uint8_t>(255 - image.row<uint8_t>(y)[x]);

            run.set_image("result", sirius::Image(narrow.data(), image.width, image.height, image.channels, SIRIUS_U8, 0, image.layout));
        }
        else
        {
            throw sirius::error("Only 8- and 16-bit images here.");
        }

        run.add_shape("spots", sirius::Shape::rect(1, 2, 3, 4).with_label("spot").with_metric("score", 0.75).with_colour(0xFFFF0000u));
        run.add_shape("spots", sirius::Shape::circle(5, 6, 2));
        run.add_shape("spots", sirius::Shape::polygon({ 0, 0, 4, 0, 4, 3 }, false));

        run.set_integer("loads", loads_);
        run.set_text("note", model_ + "/" + run.text("mode") + "/" + std::to_string(run.number("gain")).substr(0, 4) + "/" + run.text("weights"));
        run.set_bool("bright", level > 100);
        run.set_number("top", top);
    }

private:
    int loads_ = 0;
    std::string model_;
};

class Count : public sirius::Node
{
public:
    static void declare(sirius::Declaration& d)
    {
        d.name("Cpp Count").key("cpptest.count").group("Analysis");

        d.input_shapes("shapes", "Shapes");
        d.input_number("scale", "Scale").optional();
        d.input_text("prefix", "Prefix").optional();

        d.output_integer("count", "Count");
        d.output_text("labels", "Labels");
        d.output_number("scaled", "Scaled");
        d.output_shapes("echo", "Echo");
        d.output_shapes("none", "None");
    }

    void run(sirius::Run& run) override
    {
        const std::vector<sirius::Shape> shapes = run.shapes("shapes");
        std::string labels = run.text("prefix");

        for (const sirius::Shape& shape : shapes)
        {
            labels += (labels.empty() ? "" : ",") + (shape.label.empty() ? std::string("-") : shape.label);

            for (const auto& metric : shape.metrics)
                labels += ":" + metric.first;
        }

        run.set_integer("count", static_cast<long long>(shapes.size()));
        run.set_text("labels", labels);
        run.set_number("scaled", run.number("scale", 2.0) * static_cast<double>(shapes.size()));
        run.set_shapes("echo", shapes);
    }
};

class Bad : public sirius::Node
{
public:
    static void declare(sirius::Declaration& d)
    {
        d.name("Bad").key("nodots").group("Analysis");
        d.output_number("out", "Out");
    }

    void run(sirius::Run& run) override { run.set_number("out", 1); }
};

class Twice : public sirius::Node
{
public:
    static void declare(sirius::Declaration& d)
    {
        d.name("Twice").key("cpptest.twice");
        d.input_number("value", "Value");
        d.setting_number("value", "Value", 1);
        d.output_number("out", "Out");
    }

    void run(sirius::Run& run) override { run.set_number("out", 2 * run.number("value")); }
};

SIRIUS_NODES(Invert, Count, Bad, Twice)
