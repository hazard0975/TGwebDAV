import React from 'react';

export default function App() {
  return (
    <div className="min-h-screen bg-[#f0f2f5] flex items-center justify-center p-6 font-sans">
      <div className="bg-white rounded-2xl shadow-md border border-slate-100/80 p-10 md:p-12 max-w-xl w-full text-center space-y-5">
        <h1 className="text-3xl font-extrabold text-[#0f172a] tracking-tight">
          Telegram WebDAV Service
        </h1>
        <div className="text-slate-600 text-sm md:text-base leading-relaxed space-y-2">
          <p>
            Это проект на C# (Фоновый сервис для Windows).
          </p>
          <p>
            Веб-сервер запущен только в качестве заглушки, чтобы среда AI Studio корректно работала.
          </p>
        </div>
      </div>
    </div>
  );
}
