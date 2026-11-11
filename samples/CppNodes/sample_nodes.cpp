// A sample C++ node library for Sirius Studio Plus.
//
// Two nodes, in plain C++ with nothing to install beyond a compiler, so it
// builds anywhere and shows the whole interface: settings, an image in,
// shapes with measurements, numbers and an image out, a warning and an
// error. The same two nodes are in the Python sample, to compare.
//
// Build it with build.cmd beside this file, or with CMake, then copy
// SampleCppNodes.dll into the application's plugins folder - or add it in
// Tools > Custom Nodes - and the nodes appear in the toolbox.
//
// With OpenCV, a node is shorter: include <opencv2/core.hpp> before
// sirius_node.hpp and use sirius::to_mat and sirius::from_mat. See
// cpp-nodes.md.

#include "sirius_node.hpp"

#include <cstdint>
#include <string>
#include <vector>

namespace
{

// The brightness of every pixel, whatever the image is made of: the mean
// of its colour channels, in the image's own values.
std::vector<float> brightness(const sirius::Image& image)
{
    const int colours = image.channels == 4 ? 3 : image.channels;
    std::vector<float> grey(static_cast<size_t>(image.width) * image.height);

    for (int y = 0; y < image.height; y++)
    {
        float* out = grey.data() + static_cast<size_t>(y) * image.width;

        for (int x = 0; x < image.width; x++)
        {
            float sum = 0;

            for (int c = 0; c < colours; c++)
            {
                const int i = x * image.channels + c;

                switch (image.pixels)
                {
                    case SIRIUS_U8: sum += image.row<uint8_t>(y)[i]; break;
                    case SIRIUS_U16: sum += image.row<uint16_t>(y)[i]; break;
                    case SIRIUS_F32: sum += image.row<float>(y)[i]; break;
                    default:
                        throw sirius::error("This node reads 8-bit, 16-bit and float images. Convert the image first.");
                }
            }

            out[x] = sum / colours;
        }
    }

    return grey;
}

} // namespace

/// Finds spots darker than a level, and reports each as a rectangle with its area.
class DarkSpots : public sirius::Node
{
public:
    static void declare(sirius::Declaration& d)
    {
        d.name("Dark Spots (C++)")
            .key("sample.dark-spots-cpp")
            .group("Blob")
            .description("Finds spots darker than a level and reports each as a shape with its area.");

        d.input_image("image", "Image");

        d.output_shapes("spots", "Spots").description("One rectangle for each spot, labelled with its area.");
        d.output_integer("count", "Count");
        d.output_image("mask", "Mask");

        d.setting_number("level", "Level", 60)
            .range(0, 65535)
            .description("Pixels darker than this are part of a spot, in the image's own values.");

        d.setting_integer("min_area", "Min Area", 20)
            .range(0, 100000)
            .connectable()
            .description("Smaller spots are ignored, in pixels.");
    }

    void run(sirius::Run& run) override
    {
        const sirius::Image image = run.image("image");
        const float level = static_cast<float>(run.number("level"));
        const int min_area = run.integer("min_area");

        const std::vector<float> grey = brightness(image);
        const int width = image.width, height = image.height;

        std::vector<uint8_t> mask(grey.size());
        for (size_t i = 0; i < grey.size(); i++)
            mask[i] = grey[i] < level ? 255 : 0;

        // Each spot: the dark pixels that touch, found by spreading out
        // from one of them.
        std::vector<uint8_t> seen(mask.size());
        std::vector<int> pending;
        int count = 0;

        for (int start = 0; start < width * height; start++)
        {
            if (mask[start] == 0 || seen[start] != 0)
                continue;

            if (run.cancelled())
                return;

            int left = width, top = height, right = -1, bottom = -1, area = 0;
            seen[start] = 1;
            pending.assign(1, start);

            while (!pending.empty())
            {
                const int at = pending.back();
                pending.pop_back();

                const int x = at % width, y = at / width;
                area++;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;

                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        const int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                            continue;

                        const int next = ny * width + nx;
                        if (mask[next] != 0 && seen[next] == 0)
                        {
                            seen[next] = 1;
                            pending.push_back(next);
                        }
                    }
                }
            }

            if (area < min_area)
                continue;

            count++;
            run.add_shape("spots", sirius::Shape::rect(left, top, right - left + 1, bottom - top + 1)
                                       .with_label(std::to_string(area) + " px")
                                       .with_metric("area", area));
        }

        if (count == 0)
            run.warn("Nothing is darker than the level.");

        run.set_integer("count", count);
        run.set_image("mask", sirius::Image(mask.data(), width, height, 1, SIRIUS_U8));
    }
};

/// How sharp an image is: the variance of its Laplacian. A quick focus check.
class Sharpness : public sirius::Node
{
public:
    static void declare(sirius::Declaration& d)
    {
        d.name("Sharpness (C++)")
            .key("sample.sharpness-cpp")
            .group("Analysis")
            .description("Variance of the Laplacian - higher is sharper. A quick focus check.");

        d.input_image("image", "Image");

        d.output_number("sharpness", "Sharpness");
        d.output_bool("sharp", "Sharp");

        d.setting_number("minimum", "Minimum", 100)
            .range(0, 10000)
            .logarithmic()
            .description("Below this the image counts as out of focus.");
    }

    void run(sirius::Run& run) override
    {
        const sirius::Image image = run.image("image");

        if (image.width < 3 || image.height < 3)
            throw sirius::error("The image is too small to measure: it needs at least 3 x 3 pixels.");

        const std::vector<float> grey = brightness(image);
        const int width = image.width, height = image.height;

        double sum = 0, squares = 0;
        const double samples = static_cast<double>(width - 2) * (height - 2);

        for (int y = 1; y < height - 1; y++)
        {
            const float* row = grey.data() + static_cast<size_t>(y) * width;

            for (int x = 1; x < width - 1; x++)
            {
                const double edge = row[x - 1] + row[x + 1] + row[x - width] + row[x + width] - 4.0 * row[x];
                sum += edge;
                squares += edge * edge;
            }
        }

        const double mean = sum / samples;
        const double value = squares / samples - mean * mean;

        run.set_number("sharpness", value);
        run.set_bool("sharp", value >= run.number("minimum"));
    }
};

SIRIUS_NODES(DarkSpots, Sharpness)
