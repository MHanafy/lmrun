@echo off
rem lmrun - start a configured llama.cpp-compatible engine with a live prefill/generation
rem throughput dashboard. Config lives next to this file as lmrun.json.
rem
rem   run                                 run the default profile
rem   run --profile NAME                  run another profile from lmrun.json
rem   run --engine NAME / --bin PATH      pick an engine from config, or point at a binary
rem   run --list                          show configured engines and profiles
rem   run --report                        aggregate runs.jsonl by engine/version
rem   run -- serve --port 9932 llm ...    ignore the profile, pass your own engine args
setlocal
set "RUN=%~dp0lmrun.py"
python "%RUN%" %*
set "RC=%ERRORLEVEL%"
if "%RC%"=="9009" (
  py -3 "%RUN%" %*
  set "RC=%ERRORLEVEL%"
)
endlocal & exit /b %RC%
