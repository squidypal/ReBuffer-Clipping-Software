@echo off
echo ====================================
echo Building ReBuffer
echo ====================================
echo.

dotnet restore
if %errorlevel% neq 0 goto error

dotnet build -c Release
if %errorlevel% neq 0 goto error

echo.
echo ====================================
echo Build successful!
echo ====================================
echo.
echo Executable location:
echo bin\Release\net8.0-windows\ReBuffer.exe
echo.
echo IMPORTANT: Create these folders next to the EXE:
echo   - images\icon.ico (app icon)
echo   - clipSFX\clip.mp3 (save sound)
echo.
goto end

:error
echo.
echo ====================================
echo Build failed!
echo ====================================
pause
exit /b 1

:end
pause
