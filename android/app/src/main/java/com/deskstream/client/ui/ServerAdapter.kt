package com.deskstream.client.ui

import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import androidx.recyclerview.widget.RecyclerView
import com.deskstream.client.databinding.ItemServerBinding
import com.deskstream.client.net.DiscoveredServer

class ServerAdapter(
    /** Whether this server IP has a previously-paired token, shown as a small badge. */
    private val isPaired: (String) -> Boolean,
    private val onClick: (DiscoveredServer) -> Unit
) : RecyclerView.Adapter<ServerAdapter.ViewHolder>() {

    private val items = mutableListOf<DiscoveredServer>()

    /** Upserts by IP so repeated probe replies just refresh an existing row instead of
     * duplicating it. */
    fun upsert(server: DiscoveredServer) {
        val idx = items.indexOfFirst { it.ip == server.ip }
        if (idx >= 0) {
            items[idx] = server
            notifyItemChanged(idx)
        } else {
            items.add(server)
            notifyItemInserted(items.size - 1)
        }
    }

    fun clear() {
        val size = items.size
        items.clear()
        notifyItemRangeRemoved(0, size)
    }

    fun isEmpty(): Boolean = items.isEmpty()

    class ViewHolder(val binding: ItemServerBinding) : RecyclerView.ViewHolder(binding.root)

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): ViewHolder {
        val binding = ItemServerBinding.inflate(LayoutInflater.from(parent.context), parent, false)
        return ViewHolder(binding)
    }

    override fun onBindViewHolder(holder: ViewHolder, position: Int) {
        val item = items[position]
        holder.binding.tvServerName.text = item.name
        holder.binding.tvServerIp.text = "${item.ip}:${item.controlPort}"
        holder.binding.tvPairedBadge.visibility = if (isPaired(item.ip)) View.VISIBLE else View.GONE
        holder.binding.itemRoot.setOnClickListener { onClick(item) }
    }

    override fun getItemCount(): Int = items.size
}
