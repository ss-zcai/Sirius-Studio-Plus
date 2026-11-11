/*
 * A node library in plain C, straight on sirius_node.h with no C++ at all:
 * what the interface is without its wrapper. Built a second time with
 * WRONG_VERSION, as a library built for an interface this application
 * does not speak.
 */

#include "sirius_node.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static const char* nodes =
    "[{\"name\":\"C Double\",\"key\":\"ctest.double\",\"group\":\"Analysis\","
    "\"inputs\":[{\"name\":\"value\",\"label\":\"Value\",\"type\":\"number\",\"required\":true}],"
    "\"outputs\":[{\"name\":\"out\",\"label\":\"Out\",\"type\":\"number\"}],"
    "\"settings\":[{\"name\":\"times\",\"label\":\"Times\",\"type\":\"number\",\"default\":2}]}]";

static int32_t describe(const char** json, char* error, int32_t error_size)
{
    (void)error;
    (void)error_size;
    *json = nodes;
    return SIRIUS_OK;
}

static int32_t create(const char* key, void** node, char* error, int32_t error_size)
{
    if (strcmp(key, "ctest.double") != 0)
    {
        snprintf(error, (size_t)error_size, "No node '%s' here.", key);
        return SIRIUS_FAILED;
    }

    *node = malloc(1);
    return SIRIUS_OK;
}

static void destroy(void* node)
{
    free(node);
}

static int32_t run(void* node, const sirius_host* host, char* error, int32_t error_size)
{
    double value = 0, times = 0;
    (void)node;

    if (host->get_number(host->context, "value", &value) != 1 ||
        host->get_number(host->context, "times", &times) != 1 ||
        host->set_number(host->context, "out", value * times) != 1)
    {
        snprintf(error, (size_t)error_size, "%s", host->last_error(host->context));
        return SIRIUS_FAILED;
    }

    if (value < 0)
    {
        snprintf(error, (size_t)error_size, "A negative value cannot be doubled here.");
        return SIRIUS_FAILED;
    }

    return SIRIUS_OK;
}

static const sirius_library library = {
#ifdef WRONG_VERSION
    99,
#else
    SIRIUS_API_VERSION,
#endif
    (int32_t)sizeof(sirius_library), describe, create, destroy, 0, run, 0,
};

SIRIUS_EXPORT const sirius_library* sirius_node_library(void)
{
    return &library;
}
