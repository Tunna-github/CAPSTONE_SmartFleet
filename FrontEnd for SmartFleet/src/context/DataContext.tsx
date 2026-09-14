import { createContext, useContext, useState, ReactNode } from "react";
import { Task, Robot, LogEntry, TASKS, ROBOTS, INITIAL_LOGS, Priority } from "../data/mockData";

interface DataContextType {
    tasks: Task[];
    robots: Robot[];
    logs: LogEntry[];
    addTask: (pickup: string, delivery: string, pkg: string, priority: Priority) => void;
    updateTaskStatus: (id: string, status: Task["status"]) => void;
    cancelTask: (id: string) => void;
    toggleMaintenance: (id: string) => void;
    addLog: (log: Omit<LogEntry, "id" | "time">) => void;
}

const DataContext = createContext<DataContextType | undefined>(undefined);

export function DataProvider({ children }: { children: ReactNode }) {
    const [tasks, setTasks] = useState<Task[]>(TASKS);
    const [robots, setRobots] = useState<Robot[]>(ROBOTS);
    const [logs, setLogs] = useState<LogEntry[]>(INITIAL_LOGS);

    const addLog = (log: Omit<LogEntry, "id" | "time">) => {
        setLogs(prev => [{
            ...log,
            id: Date.now(),
            time: new Date().toLocaleTimeString("en-GB", { hour12: false })
        }, ...prev].slice(0, 50));
    };

    const addTask = (pickup: string, delivery: string, packageInfo: string, priority: Priority) => {
        const newId = `TSK-${String(Math.floor(Math.random() * 9000) + 1000)}`;
        const newTask: Task = {
            id: newId,
            pickup,
            delivery,
            packageInfo,
            priority,
            robotId: null,
            status: "PENDING",
            created: new Date().toLocaleTimeString("en-GB", { hour: "2-digit", minute: "2-digit" })
        };
        setTasks(prev => [newTask, ...prev]);
        addLog({ level: "INFO", robot: "SYSTEM", message: `New task ${newId} created: ${pickup} → ${delivery}` });
    };

    const updateTaskStatus = (id: string, status: Task["status"]) => {
        setTasks(prev => prev.map(t => t.id === id ? { ...t, status } : t));
    };

    const cancelTask = (id: string) => {
        setTasks(prev => prev.map(t => t.id === id ? { ...t, status: "CANCELLED", robotId: null } : t));
        // Free up the robot
        setRobots(prev => prev.map(r => r.missionId === id ? { ...r, missionId: null, zone: "Idle — Zone B" } : r));
        addLog({ level: "WARN", robot: "SYSTEM", message: `Task ${id} cancelled. Robot reassigned.` });
    };

    const toggleMaintenance = (id: string) => {
        setRobots(prev => prev.map(r => {
            if (r.id === id) {
                const newState = !r.maintenance;
                addLog({ level: "INFO", robot: id, message: `Maintenance mode ${newState ? "ENABLED" : "DISABLED"}` });
                return { ...r, maintenance: newState, mqtt: newState ? "OFFLINE" : "ONLINE" };
            }
            return r;
        }));
    };

    return (
        <DataContext.Provider value={{ tasks, robots, logs, addTask, updateTaskStatus, cancelTask, toggleMaintenance, addLog }}>
            {children}
        </DataContext.Provider>
    );
}

export function useData() {
    const context = useContext(DataContext);
    if (!context) throw new Error("useData must be used within DataProvider");
    return context;
}