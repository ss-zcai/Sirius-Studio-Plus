// A stand-in for OpenCV's <opencv2/core.hpp>, with only what
// sirius::to_mat and sirius::from_mat touch, shaped as OpenCV shapes it.
// It lets the tests compile that glue on a PC with no OpenCV installed; it
// is not OpenCV, and proves nothing about OpenCV itself.

#ifndef OPENCV_CORE_HPP
#define OPENCV_CORE_HPP

#include <cstddef>

#define CV_CN_SHIFT 3
#define CV_DEPTH_MAX (1 << CV_CN_SHIFT)
#define CV_MAT_DEPTH_MASK (CV_DEPTH_MAX - 1)
#define CV_MAT_DEPTH(flags) ((flags) & CV_MAT_DEPTH_MASK)
#define CV_MAKETYPE(depth, cn) (CV_MAT_DEPTH(depth) + (((cn) - 1) << CV_CN_SHIFT))

typedef unsigned char uchar;

namespace cv
{

struct MatStep
{
    size_t value = 0;
    operator size_t() const { return value; }
};

class Mat
{
public:
    Mat() {}

    Mat(int rows_, int cols_, int type, void* data_, size_t step_ = 0)
        : rows(rows_), cols(cols_), data(static_cast<uchar*>(data_)), flags(type)
    {
        step.value = step_;
    }

    int channels() const { return (flags >> CV_CN_SHIFT) + 1; }
    int depth() const { return CV_MAT_DEPTH(flags); }
    bool empty() const { return data == nullptr; }

    int rows = 0, cols = 0;
    uchar* data = nullptr;
    MatStep step;

private:
    int flags = 0;
};

} // namespace cv

#endif
