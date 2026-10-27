call "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
if errorlevel 1 exit /b 1
mkdir "n:\AI Crap\HowToFish-VRmod\build\native" 2>nul
cl.exe /nologo /O2 /EHsc /MD /std:c++17 /W3 ^
  /I"n:\AI Crap\HowToFish-VRmod\third_party\openxr\include" ^
  "n:\AI Crap\HowToFish-VRmod\native\HowToFishVRNative.cpp" ^
  /link /DLL ^
  /LIBPATH:"n:\AI Crap\HowToFish-VRmod\third_party\openxr\x64\lib" ^
  openxr_loader.lib d3d11.lib dxgi.lib user32.lib ^
  /OUT:"n:\AI Crap\HowToFish-VRmod\build\native\HowToFishVRNative.dll"
