// Compiles sirius::to_mat and sirius::from_mat - against the stand-in
// beside this file where there is no OpenCV - and passes an image through
// both.

#include <opencv2/core.hpp>

#include "sirius_node.hpp"

class Through : public sirius::Node
{
public:
    static void declare(sirius::Declaration& d)
    {
        d.name("Cpp Through").key("cpptest.through");
        d.input_image("image", "Image");
        d.output_image("result", "Result");
        d.output_integer("type", "Type");
    }

    void run(sirius::Run& run) override
    {
        const sirius::Image image = run.image("image");
        cv::Mat mat = sirius::to_mat(image);

        run.set_integer("type", mat.depth() * 10 + mat.channels());
        run.set_image("result", sirius::from_mat(mat, image.layout, image.bits));
    }
};

SIRIUS_NODES(Through)
