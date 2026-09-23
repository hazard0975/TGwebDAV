# Project Rules & Instructions for AI Assistant

## Core Principles
1. **Always keep `README.md` updated**:
   - Update `README.md` whenever the project architecture, dependencies, or core specs change.
   - Ensure `README.md` remains a clean, high-level overview and technical specification of the project.

2. **Maintain `ROADMAP.md`**:
   - `ROADMAP.md` is the primary tracking document for stages, completed tasks, current work, and upcoming features.
   - Update task statuses in `ROADMAP.md` as tasks are completed or modified.

3. **User Intent & Safety**:
   - Do NOT modify codebase unless the user explicitly gives consent with the keyword `"делай"`.
   - Proactively suggest optimizations, point out potential bottlenecks, risks, or edge cases.

4. **Обязательная компиляция и валидация C# проекта**:
   - При любых изменениях C# кода обязательно запускать сборку и валидацию:
     ```bash
     dotnet build /app/applet/TelegramWebDAV/TelegramWebDAV.csproj -p:EnableWindowsTargeting=true
     ```
   - Если .NET SDK отсутствует в сессии (новый контейнер), установка выполняется командой:
     ```bash
     curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh && chmod +x /tmp/dotnet-install.sh && /tmp/dotnet-install.sh --channel 8.0 --install-dir /usr/share/dotnet && ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet
     ```

