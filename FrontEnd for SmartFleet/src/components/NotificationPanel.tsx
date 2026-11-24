import { useEffect, useState } from "react";
import { apiClient } from "../api/client";

interface Notice { notificationId: number; isRead: boolean; title: string; message: string; severity: string; createdAt: string; }

export default function NotificationPanel({ collapsed }: { collapsed: boolean }) {
    const [items, setItems] = useState<Notice[]>([]);
    const [open, setOpen] = useState(false);
    const [error, setError] = useState("");
    const load = async () => {
        try { const { data } = await apiClient.get<Notice[]>("/api/v1/notifications"); setItems(data); setError(""); }
        catch { setError("Notifications are unavailable."); }
    };
    useEffect(() => { void load(); const timer = setInterval(() => void load(), 15000); return () => clearInterval(timer); }, []);
    const read = async (id: number) => {
        try { await apiClient.post(`/api/v1/notifications/${id}/read`); await load(); }
        catch { setError("Could not mark notification as read."); }
    };
    return <div className="px-3 pb-3">
        <button className="w-full rounded-lg border px-2 py-2 text-xs" aria-label="Notifications" aria-expanded={open} onClick={() => setOpen(!open)}>
            {collapsed ? "!" : "Notifications"} ({items.filter(i => !i.isRead).length})
        </button>
        {open && <section aria-label="Notification history" className="fixed right-4 top-4 z-[90] max-h-[80vh] overflow-auto w-[min(480px,90vw)] rounded-xl border p-4 shadow-xl" style={{ background: "var(--surface-2)" }}>
            <div className="flex justify-between"><strong>Notifications</strong><button onClick={() => setOpen(false)} aria-label="Close notifications">✕</button></div>
            {error && <p role="alert">{error}</p>}
            {!items.length && !error && <p className="py-3 text-sm">No notifications.</p>}
            {items.map(item => <article className="border-t py-3 text-sm" key={item.notificationId}>
                <strong>{item.title}</strong><p>{item.message}</p><p className="text-xs opacity-60">{item.severity} · {new Date(item.createdAt).toLocaleString()}</p>
                {!item.isRead && <button className="mt-2 rounded border px-2 py-1" onClick={() => void read(item.notificationId)}>Mark as read</button>}
            </article>)}
        </section>}
    </div>;
}
