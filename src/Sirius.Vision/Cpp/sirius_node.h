/*
 * sirius_node.h - the interface between Sirius Studio Plus and a custom node
 * library written in C or C++.
 *
 * A library is an ordinary 64-bit DLL that exports one function,
 * sirius_node_library. Nothing in it is .NET, and nothing here depends on a
 * compiler or a C++ runtime: plain C types and function pointers, so a
 * library built with any compiler, against any OpenCV, HALCON or deep
 * learning runtime, loads into any version of the application that speaks
 * this interface version.
 *
 * Most libraries never use this file directly. sirius_node.hpp, beside it,
 * wraps it in a few C++ classes and - as important - catches what goes
 * wrong: C++ exceptions and hardware faults both come back to the
 * application as the node's error rather than ending the process.
 *
 * The library runs inside the application's process. That is what makes it
 * as fast as a built-in tool: an image arrives as a pointer to the
 * application's own memory, with no copy.
 */

#ifndef SIRIUS_NODE_H
#define SIRIUS_NODE_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* The interface version. The application loads a library only when this matches. */
#define SIRIUS_API_VERSION 1

#if defined(_WIN32)
#define SIRIUS_EXPORT __declspec(dllexport)
#else
#define SIRIUS_EXPORT __attribute__((visibility("default")))
#endif

/* ---- results ------------------------------------------------------------ */

/* What create, load and run return. */
#define SIRIUS_OK 0      /* done */
#define SIRIUS_FAILED 1  /* could not be done; the error text says why */
#define SIRIUS_CRASHED 2 /* a hardware fault was caught; the library should not be used again */

/* ---- images ------------------------------------------------------------- */

/* How each sample is stored. The numbers are OpenCV's depths, so
   CV_MAKETYPE(image.pixels, image.channels) is the matrix type. */
#define SIRIUS_U8 0
#define SIRIUS_S8 1
#define SIRIUS_U16 2
#define SIRIUS_S16 3
#define SIRIUS_S32 4
#define SIRIUS_F32 5
#define SIRIUS_F64 6

/* What the channels mean. Three channels are not always blue, green, red:
   after a Colour Convert they may be hue, saturation and value. */
#define SIRIUS_LAYOUT_UNKNOWN 0 /* taken to be what the channel count suggests */
#define SIRIUS_LAYOUT_GRAY 1
#define SIRIUS_LAYOUT_BGR 2
#define SIRIUS_LAYOUT_BGRA 3
#define SIRIUS_LAYOUT_HSV 4
#define SIRIUS_LAYOUT_HLS 5
#define SIRIUS_LAYOUT_LAB 6
#define SIRIUS_LAYOUT_LUV 7
#define SIRIUS_LAYOUT_YCRCB 8
#define SIRIUS_LAYOUT_XYZ 9

/*
 * An image: interleaved samples, rows top to bottom.
 *
 * One read from an input is the application's own memory. It is read-only -
 * every other node wired to the same output is reading it too - and it is
 * valid only until run returns. One given to set_image is copied before
 * set_image returns, so it may be freed or reused straight after.
 */
typedef struct sirius_image
{
    int32_t width;    /* pixels */
    int32_t height;   /* pixels */
    int32_t channels; /* samples per pixel, 1 to 4 */
    int32_t pixels;   /* SIRIUS_U8 ... SIRIUS_F64 */
    int32_t layout;   /* SIRIUS_LAYOUT_... */
    int32_t bits;     /* bits that carry the picture: 12 for a 12-bit camera's
                         frame in 16-bit samples. 0 for all of the type, and
                         for floating point. */
    int64_t stride;   /* bytes from the start of one row to the start of the
                         next; at least width * channels * the sample size */
    void* data;       /* the first sample of the top row */
} sirius_image;

/* ---- shapes ------------------------------------------------------------- */

/* The kinds of shape, and what v holds for each, in pixels and degrees. */
#define SIRIUS_POINT 0   /* x, y */
#define SIRIUS_LINE 1    /* x1, y1, x2, y2 */
#define SIRIUS_RECT 2    /* x, y, width, height - x, y the top left corner */
#define SIRIUS_BOX 3     /* centre x, centre y, width, height, angle - a turned rectangle */
#define SIRIUS_CIRCLE 4  /* centre x, centre y, radius */
#define SIRIUS_ELLIPSE 5 /* centre x, centre y, major axis, minor axis, angle */
#define SIRIUS_POLYGON 6 /* nothing in v; points holds x0, y0, x1, y1, ... */

/* A measurement carried by a shape: "area", 1520. */
typedef struct sirius_metric
{
    const char* name; /* UTF-8 */
    double value;
} sirius_metric;

/*
 * A shape, in the coordinates of the image it was found in.
 *
 * One read with get_shape points at memory that is valid until the next
 * get_shape or until run returns. One given to add_shape is copied.
 */
typedef struct sirius_shape
{
    int32_t kind;                 /* SIRIUS_POINT ... SIRIUS_POLYGON */
    int32_t closed;               /* a polygon: 1 closed, 0 an open line */
    double v[5];                  /* by kind, as above */
    const double* points;         /* a polygon's x, y pairs; otherwise null */
    int32_t point_count;          /* how many points - pairs, not doubles */
    int32_t metric_count;
    const sirius_metric* metrics; /* or null */
    const char* label;            /* UTF-8, or null for none */
    uint32_t colour;              /* 0xAARRGGBB; 0 for the default */
    int32_t reserved;
} sirius_shape;

/* ---- what a node is given while it runs ---------------------------------- */

/*
 * The application's side of a run: where a node reads its settings and
 * inputs, and puts its outputs.
 *
 * Every function takes context first. A name is the name the node declared
 * - of a setting, an input or an output - in UTF-8.
 *
 * The get functions return 1 when they wrote a value, 0 when the name is an
 * input with nothing wired to it, and -1 when the call was wrong: no such
 * name, or one of another type. The set functions return 1, or -1. After a
 * -1, last_error says why.
 *
 * Pointers handed out - a text, an image's data, a shape - belong to the
 * application and are valid until run returns. Nothing here may be called
 * after it has, or from another thread.
 */
typedef struct sirius_host
{
    int32_t size; /* bytes of this structure the application filled in */
    int32_t reserved;
    void* context;

    /* 1 for a setting or a wired input, 0 for an input with nothing wired, -1 for no such name. */
    int32_t (*has)(void* context, const char* name);

    int32_t (*get_number)(void* context, const char* name, double* value);
    int32_t (*get_integer)(void* context, const char* name, int64_t* value);
    int32_t (*get_bool)(void* context, const char* name, int32_t* value);
    int32_t (*get_text)(void* context, const char* name, const char** value);
    int32_t (*get_image)(void* context, const char* name, sirius_image* image);
    int32_t (*get_shape_count)(void* context, const char* name, int32_t* count);
    int32_t (*get_shape)(void* context, const char* name, int32_t index, sirius_shape* shape);

    int32_t (*set_number)(void* context, const char* name, double value);
    int32_t (*set_integer)(void* context, const char* name, int64_t value);
    int32_t (*set_bool)(void* context, const char* name, int32_t value);
    int32_t (*set_text)(void* context, const char* name, const char* value);
    int32_t (*set_image)(void* context, const char* name, const sirius_image* image);
    int32_t (*add_shape)(void* context, const char* name, const sirius_shape* shape);

    /* Shown on the node in amber; the run still succeeds. */
    void (*warn)(void* context, const char* message);

    /* 1 once the run has been cancelled: stop when convenient and return. */
    int32_t (*cancelled)(void* context);

    /* Why the last call returned -1. Valid until the next call. */
    const char* (*last_error)(void* context);

    /* The folder the library's DLL is in, for a model file kept beside it. */
    const char* (*library_folder)(void* context);
} sirius_host;

/* ---- what a library provides --------------------------------------------- */

/*
 * A library's entry points.
 *
 * error is a buffer of error_size bytes for a message in UTF-8, ended with a
 * zero, saying what went wrong when the result is not SIRIUS_OK.
 *
 * The application never calls into one library from two threads at once,
 * but successive calls may come from different threads.
 */
typedef struct sirius_library
{
    int32_t api_version; /* SIRIUS_API_VERSION */
    int32_t size;        /* sizeof(sirius_library) */

    /*
     * The library's nodes, as JSON in UTF-8 that stays valid for as long as
     * the library is loaded:
     *
     *   [ { "name": "Dark Spots", "key": "acme.dark-spots", "group": "Blob",
     *       "description": "...", "icon": "...",
     *       "inputs":   [ { "name": "image", "label": "Image", "type": "image", "required": true } ],
     *       "outputs":  [ { "name": "count", "label": "Count", "type": "integer" } ],
     *       "settings": [ { "name": "level", "label": "Level", "type": "integer", "default": 60,
     *                       "min": 0, "max": 255, "description": "...", "group": "...",
     *                       "connectable": false, "reload": false } ] } ]
     *
     * Port types: image, number, integer, bool, text, shapes. Setting types:
     * number, integer, bool, text, choice (with "choices": [...]) and file
     * (with "filter": "Models|*.onnx").
     */
    int32_t (*describe)(const char** json, char* error, int32_t error_size);

    /* Makes one node of the kind with this key - one for each on the canvas. */
    int32_t (*create)(const char* key, void** node, char* error, int32_t error_size);

    /* Frees a node made by create. */
    void (*destroy)(void* node);

    /*
     * Called before a node's first run, and again whenever a setting
     * declared with "reload" has changed: the place to load a model. Only
     * settings can be read. May be null.
     */
    int32_t (*load)(void* node, const sirius_host* host, char* error, int32_t error_size);

    /* Runs a node once. */
    int32_t (*run)(void* node, const sirius_host* host, char* error, int32_t error_size);

    /*
     * Copies bytes, returning SIRIUS_CRASHED instead of faulting when either
     * end is not memory that can be used. The application copies an output
     * image through it, so a pointer that has gone stale is the node's
     * error and not the application's crash. May be null.
     */
    int32_t (*copy)(void* destination, const void* source, int64_t bytes);
} sirius_library;

/* The one function a library exports. Returns the same structure every time. */
typedef const sirius_library* (*sirius_node_library_function)(void);

#define SIRIUS_ENTRY_POINT "sirius_node_library"

#ifdef __cplusplus
}
#endif

#endif /* SIRIUS_NODE_H */
