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

5. **Непрерывность истории и ведение журналов (`DECISIONS_LOG.md` и `ROADMAP.md`)**:
   - Не удалять, затирать или переписывать прошлые архитектурные решения и попытки.
   - Любое новое решение или изменение подхода добавляется новым отдельным подпунктом/номером в конец журнала для ослеживания прозрачной истории.
   - Если предыдущий вариант не сработал или вызвал проблему, он не удаляется, а помечается статусом (например, "Не сработало", "Решили переделать на ..." "Вызвало баг при таких-то условиях"), с сохранением описания гипотезы и первопричины сбоя, чтобы не ходить по кругу и иметь возможность вернуться к доработке гипотезы в будущем, а также иметь полную историю действий и решений.




