import { createContext, useContext, useEffect, useState, ReactNode, useCallback } from "react";
import { Task, Robot, LogEntry, TASKS, ROBOTS, INITIAL_LOGS, Priority } from "../data/mockData";
import { getTransportTasks } from "../api/modules/tasks";

interface DataContextType {
    tasks: Task[];
    robots: Robot[];
    logs: LogEntry[];
    isTasksLoading: boolean;
    tasksError: string | null;
    addTask: (pickup: string, delivery: string, pkg: string, priority: Priority) => void;
    refreshTasks: (showErrorToast?: boolean) => Promise<void>;
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
    const [isTasksLoading, setIsTasksLoading] = useState(false);
    const [tasksError, setTasksError] = useState<string | null>(null);

    const addLog = useCallback((log: Omit<LogEntry, "id" | "time">) => {
        setLogs(prev => [{
            ...log,
            id: Date.now(),
            time: new Date().toLocaleTimeString("en-GB", { hour12: false })
        }, ...prev].slice(0, 50));
    }, []);

    const refreshTasks = useCallback(async (showErrorToast = false) => {
        setIsTasksLoading(true);
        setTasksError(null);

        try {
            const apiTasks = await getTransportTasks();
            setTasks(apiTasks);
        } catch (error) {
            const message = error instanceof Error ? error.message : "Failed to load tasks from API.";
            setTasksError(message);

            if (showErrorToast) {
                addLog({ level: "ERROR", robot: "SYSTEM", message });
            }

            throw error;
        } finally {
            setIsTasksLoading(false);
        }
    }, [addLog]);

    useEffect(() => {
        let isMounted = true;

        async function loadTasks() {
            try {
                const apiTasks = await getTransportTasks();

                if (isMounted) {
                    setTasks(apiTasks);
                    setTasksError(null);
                }
            } catch (error) {
                if (isMounted) {
                    const message = error instanceof Error ? error.message : "Failed to load tasks from API.";
                    setTasksError(message);
                }
            } finally {
                if (isMounted) {
                    setIsTasksLoading(false);
                }
            }
        }

        setIsTasksLoading(true);
        loadTasks();

        return () => {
            isMounted = false;
        };
    }, []);

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
        <DataContext.Provider
            value={{
                tasks,
                robots,
                logs,
                isTasksLoading,
                tasksError,
                addTask,
                refreshTasks,
                updateTaskStatus,
                cancelTask,
                toggleMaintenance,
                addLog,
            }}
        >
            {children}
        </DataContext.Provider>
    );
}

export function useData() {
    const context = useContext(DataContext);
    if (!context) throw new Error("useData must be used within DataProvider");
    return context;
}
