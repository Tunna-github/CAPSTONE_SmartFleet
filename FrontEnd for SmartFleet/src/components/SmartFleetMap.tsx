import { useCallback, useState } from 'react';
import ReactFlow, {
  Background,
  Controls,
  MiniMap,
  addEdge,
  applyNodeChanges,
  applyEdgeChanges,
  Node,
  Edge,
  Connection,
  NodeChange,
  EdgeChange,
} from 'reactflow';
import 'reactflow/dist/style.css';

// Khởi tạo 9 node (Mã QR) theo dạng lưới 3x3 (Khoảng cách 200px tượng trưng cho 1m)
const initialNodes: Node[] = [
  { id: 'qr-1', position: { x: 0, y: 0 }, data: { label: 'QR 1 (0,0)' } },
  { id: 'qr-2', position: { x: 200, y: 0 }, data: { label: 'QR 2 (1,0)' } },
  { id: 'qr-3', position: { x: 400, y: 0 }, data: { label: 'QR 3 (2,0)' } },
  { id: 'qr-4', position: { x: 0, y: 200 }, data: { label: 'QR 4 (0,1)' } },
  { id: 'qr-5', position: { x: 200, y: 200 }, data: { label: 'QR 5 (1,1)' } },
  { id: 'qr-6', position: { x: 400, y: 200 }, data: { label: 'QR 6 (2,1)' } },
  { id: 'qr-7', position: { x: 0, y: 400 }, data: { label: 'QR 7 (0,2)' } },
  { id: 'qr-8', position: { x: 200, y: 400 }, data: { label: 'QR 8 (1,2)' } },
  { id: 'qr-9', position: { x: 400, y: 400 }, data: { label: 'QR 9 (2,2)' } },
];

// Khởi tạo một số tuyến đường (Route Segments)
const initialEdges: Edge[] = [
  { id: 'e1-2', source: 'qr-1', target: 'qr-2', animated: true, style: { stroke: '#e65100', strokeWidth: 2 } },
  { id: 'e2-3', source: 'qr-2', target: 'qr-3', animated: true, style: { stroke: '#e65100', strokeWidth: 2 } },
  { id: 'e1-4', source: 'qr-1', target: 'qr-4', animated: true, style: { stroke: '#e65100', strokeWidth: 2 } },
  { id: 'e4-5', source: 'qr-4', target: 'qr-5' },
];

export default function SmartFleetMap() {
  const [nodes, setNodes] = useState<Node[]>(initialNodes);
  const [edges, setEdges] = useState<Edge[]>(initialEdges);

  // Xử lý sự kiện kéo thả node
  const onNodesChange = useCallback(
    (changes: NodeChange[]) => setNodes((nds) => applyNodeChanges(changes, nds)),
    []
  );

  // Xử lý sự kiện thay đổi cạnh (xóa, thêm)
  const onEdgesChange = useCallback(
    (changes: EdgeChange[]) => setEdges((eds) => applyEdgeChanges(changes, eds)),
    []
  );

  // Xử lý sự kiện người dùng kéo nối 2 node với nhau để tạo đường đi mới
  const onConnect = useCallback(
    (connection: Connection) => setEdges((eds) => addEdge({ ...connection, animated: true }, eds)),
    []
  );

  return (
    <div style={{ width: '100%', height: '80vh', border: '1px solid #ddd', borderRadius: '8px' }}>
      <ReactFlow
        nodes={nodes}
        edges={edges}
        onNodesChange={onNodesChange}
        onEdgesChange={onEdgesChange}
        onConnect={onConnect}
        fitView
      >
        <Controls />
        <MiniMap zoomable pannable />
        <Background color="#aaa" gap={16} />
      </ReactFlow>
    </div>
  );
}