import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useState,
  type Dispatch,
  type ReactNode,
  type SetStateAction,
} from "react";
import { useAuth } from "./AuthContext";
import {
  getNavigationMap,
  getCommissionedMapTemplate,
  putNavigationMap,
  navigationErrorMessage,
  type NavigationMapDocument,
  type NavigationMapCell,
} from "../api/modules/navigation";
import {
  createDefaultWarehouseLayout,
  type WarehouseCell,
  type WarehouseLayout,
} from "../components/WarehouseGrid";

interface WarehouseMapState {
  warehouseLayout: WarehouseLayout;
  setWarehouseLayout: Dispatch<SetStateAction<WarehouseLayout>>;
  links: Array<[string, string]>;
  setLinks: Dispatch<SetStateAction<Array<[string, string]>>>;
  mapRevision: number | null;
  directedLinks: boolean;
  markerDictionary: "DICT_4X4_50" | "DICT_4X4_250";
  setDirectedLinks: Dispatch<SetStateAction<boolean>>;
  setMarkerDictionary: Dispatch<SetStateAction<"DICT_4X4_50" | "DICT_4X4_250">>;
  mapDirty: boolean;
  mapLoading: boolean;
  mapSaving: boolean;
  mapError: string | null;
  reloadMap: () => Promise<void>;
  saveMap: () => Promise<void>;
  loadCommissionedTemplate: () => Promise<void>;
}

export function computeDefaultLinks(layout: WarehouseLayout): Array<[string, string]> {
  const rows = new Map<number, Array<{ label: string; c: number }>>();
  const cols = new Map<number, Array<{ label: string; r: number }>>();
  layout.forEach((row, r) => row.forEach((cell, c) => {
    if (!cell.label || !["station", "qr", "charge"].includes(cell.type)) return;
    rows.set(r, [...(rows.get(r) ?? []), { label: cell.label, c }]);
    cols.set(c, [...(cols.get(c) ?? []), { label: cell.label, r }]);
  }));
  const links: Array<[string, string]> = [];
  for (const row of rows.values()) {
    row.sort((a, b) => a.c - b.c);
    for (let i = 1; i < row.length; i++) links.push([row[i - 1].label, row[i].label]);
  }
  for (const col of cols.values()) {
    col.sort((a, b) => a.r - b.r);
    for (let i = 1; i < col.length; i++) links.push([col[i - 1].label, col[i].label]);
  }
  return links;
}

function mapFromBackend(document: NavigationMapDocument): WarehouseLayout {
  if (document.rows !== 18 || document.columns !== 28 ||
      document.cells.length !== 18 || document.cells.some(row => row.length !== 28)) {
    throw new Error("Backend map must be exactly 18 rows by 28 columns.");
  }
  return document.cells.map(row => row.map(cell => ({
    type: cell.type,
    label: cell.label ?? undefined,
    displayName: cell.displayName ?? undefined,
    operationalRole: cell.operationalRole ?? undefined,
    arucoId: cell.arucoId ?? undefined,
    isIntersection: cell.isIntersection,
    markerYawDeg: cell.markerYawDeg ?? undefined,
    markerSizeM: cell.markerSizeM ?? undefined,
  } satisfies WarehouseCell)));
}

function arucoFromCell(cell: WarehouseCell, maxId: number): number {
  const idFromLabel = /^QR0*([0-9]+)$/i.exec(cell.label ?? "");
  const id = cell.arucoId ?? (idFromLabel ? Number(idFromLabel[1]) : undefined);
  if (id === undefined || !Number.isInteger(id) || id < 0 || id > maxId) {
    throw new Error(`QR marker ${cell.label ?? "(unnamed)"} needs an ArUco ID from 0 to ${maxId}.`);
  }
  return id;
}

function mapToBackend(
  layout: WarehouseLayout,
  links: Array<[string, string]>,
  revision: number,
  directedLinks: boolean,
  markerDictionary: "DICT_4X4_50" | "DICT_4X4_250",
): NavigationMapDocument {
  const seenIds = new Set<number>();
  const cells: NavigationMapCell[][] = layout.map(row => row.map(cell => {
    const type = cell.type;
    const point = type === "qr" || type === "station" || type === "charge";
    const arucoId = type === "qr" ? arucoFromCell(cell,markerDictionary === "DICT_4X4_250" ? 249 : 49) : null;
    if (arucoId !== null) {
      if (seenIds.has(arucoId)) throw new Error(`Duplicate ArUco ID ${arucoId}. Each marker needs a unique ID.`);
      seenIds.add(arucoId);
    }
    return {
      type,
      label: point ? (cell.label?.trim() || null) : null,
      displayName: type === "qr" ? cell.displayName?.trim() || null : null,
      operationalRole: type === "qr" ? cell.operationalRole ?? null : null,
      arucoId,
      isIntersection: type === "qr" && !!cell.isIntersection,
      markerYawDeg: type === "qr" ? cell.markerYawDeg ?? null : null,
      markerSizeM: type === "qr" ? cell.markerSizeM ?? null : null,
    };
  }));
  return { revision, rows: 18, columns: 28, cells, directedLinks, markerDictionary, links: links.map(([a, b]) => [a, b]) };
}

const WarehouseMapContext = createContext<WarehouseMapState | null>(null);

export default function WarehouseMapProvider({ children }: { children: ReactNode }) {
  const { isAuthenticated, role, isLoading: authLoading } = useAuth();
  const [warehouseLayout, setWarehouseLayout] = useState(createDefaultWarehouseLayout);
  const [links, setLinks] = useState(() => computeDefaultLinks(warehouseLayout));
  const [mapRevision, setMapRevision] = useState<number | null>(null);
  const [directedLinks,setDirectedLinks] = useState(false);
  const [markerDictionary,setMarkerDictionary] = useState<"DICT_4X4_50" | "DICT_4X4_250">("DICT_4X4_50");
  const [savedSettings,setSavedSettings] = useState("");
  const [savedMap, setSavedMap] = useState<{ layout: WarehouseLayout; links: Array<[string, string]> } | null>(null);
  const [mapLoading, setMapLoading] = useState(false);
  const [mapSaving, setMapSaving] = useState(false);
  const [mapError, setMapError] = useState<string | null>(null);

  const applySnapshot = useCallback((snapshot: NavigationMapDocument) => {
    const layout = mapFromBackend(snapshot);
    const snapshotLinks = snapshot.links.map(link => [link[0], link[1]] as [string, string]);
    setWarehouseLayout(layout);
    setLinks(snapshotLinks);
    setSavedMap({ layout, links: snapshotLinks });
    setMapRevision(snapshot.revision);
    setDirectedLinks(!!snapshot.directedLinks);
    setMarkerDictionary(snapshot.markerDictionary ?? "DICT_4X4_50");
    setSavedSettings(JSON.stringify([!!snapshot.directedLinks,snapshot.markerDictionary ?? "DICT_4X4_50"]));
    setMapError(null);
  }, []);

  const reloadMap = useCallback(async () => {
    if (!isAuthenticated || !["admin", "operator", "maintenance"].includes(role ?? "")) return;
    setMapLoading(true);
    try {
      applySnapshot(await getNavigationMap());
    } catch (error) {
      const msg = navigationErrorMessage(error);
      setMapError(msg);
      throw error;
    } finally {
      setMapLoading(false);
    }
  }, [applySnapshot, isAuthenticated, role]);

  useEffect(() => {
    if (authLoading || !isAuthenticated || !["admin", "operator", "maintenance"].includes(role ?? "")) return;
    void reloadMap().catch(() => { /* Error is shown in mapError. */ });
  }, [authLoading, isAuthenticated, role, reloadMap]);

  const saveMap = useCallback(async () => {
    if (role !== "admin") throw new Error("Only an Administrator can save the map.");
    if (mapRevision === null) throw new Error("Load the Backend map before saving changes.");
    if (mapLoading) throw new Error("Wait until the Backend map finishes loading.");
    setMapSaving(true);
    try {
      // Save only on explicit click; never push partial edits automatically.
      const request = mapToBackend(warehouseLayout, links, mapRevision,directedLinks,markerDictionary);
      applySnapshot(await putNavigationMap(request));
    } catch (error) {
      const msg = navigationErrorMessage(error);
      setMapError(msg);
      throw error;
    } finally {
      setMapSaving(false);
    }
  }, [applySnapshot, links, mapLoading, mapRevision, role, warehouseLayout,directedLinks,markerDictionary]);
  const loadCommissionedTemplate=useCallback(async()=>{
    if(role!=="admin" || mapLoading || mapSaving) throw new Error("Load the Admin map before choosing its scene template.");
    const template=await getCommissionedMapTemplate();
    setWarehouseLayout(mapFromBackend(template));setLinks(template.links.map(e=>[e[0],e[1]]));
    setDirectedLinks(!!template.directedLinks);setMarkerDictionary(template.markerDictionary??"DICT_4X4_50");
  },[role,mapLoading,mapSaving]);

  return (
    <WarehouseMapContext.Provider value={{
      warehouseLayout, setWarehouseLayout, links, setLinks,
      directedLinks,setDirectedLinks,markerDictionary,setMarkerDictionary,
      mapDirty: savedMap !== null && (warehouseLayout !== savedMap.layout || links !== savedMap.links || savedSettings !== JSON.stringify([directedLinks,markerDictionary])),
      mapRevision, mapLoading, mapSaving, mapError, reloadMap, saveMap,loadCommissionedTemplate,
    }}>
      {children}
    </WarehouseMapContext.Provider>
  );
}

export function useWarehouseMap(): WarehouseMapState {
  const context = useContext(WarehouseMapContext);
  if (!context) throw new Error("useWarehouseMap must be used within WarehouseMapProvider");
  return context;
}
