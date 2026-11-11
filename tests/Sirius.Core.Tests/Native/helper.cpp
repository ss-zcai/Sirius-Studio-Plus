// A DLL the "needs" library depends on - there to be taken away.
extern "C" __declspec(dllexport) int sirius_test_helper_value(void)
{
    return 21;
}
