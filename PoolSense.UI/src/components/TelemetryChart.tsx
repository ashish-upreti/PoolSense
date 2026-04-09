import {
  Bar,
  BarChart,
  CartesianGrid,
  Cell,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts'

export interface TelemetryDatum {
  name: string
  value: number
  color: string
}

interface TelemetryChartProps {
  data: TelemetryDatum[]
}

export default function TelemetryChart({ data }: TelemetryChartProps) {
  return (
    <ResponsiveContainer width="100%" height="100%">
      <BarChart data={data} barSize={28}>
        <CartesianGrid vertical={false} stroke="rgba(53, 78, 106, 0.08)" />
        <XAxis dataKey="name" tickLine={false} axisLine={false} tickMargin={10} />
        <YAxis hide domain={[0, 100]} />
        <Tooltip cursor={{ fill: 'rgba(47, 121, 200, 0.06)' }} />
        <Bar dataKey="value" radius={[10, 10, 6, 6]}>
          {data.map((entry) => (
            <Cell key={entry.name} fill={entry.color} />
          ))}
        </Bar>
      </BarChart>
    </ResponsiveContainer>
  )
}