// A node library that needs another DLL beside it, as one built on OpenCV
// needs OpenCV's.

#include "sirius_node.hpp"

extern "C" __declspec(dllimport) int sirius_test_helper_value(void);

class Needs : public sirius::Node
{
public:
    static void declare(sirius::Declaration& d)
    {
        d.name("Cpp Needs").key("cpptest.needs");
        d.output_integer("out", "Out");
    }

    void run(sirius::Run& run) override { run.set_integer("out", 2 * sirius_test_helper_value()); }
};

SIRIUS_NODES(Needs)
