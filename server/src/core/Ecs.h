#pragma once

#include <cstdint>
#include <functional>
#include <memory>
#include <typeindex>
#include <unordered_map>
#include <utility>
#include <vector>

namespace coop {

using Entity = std::uint32_t;
inline constexpr Entity kInvalidEntity = 0;

/// Minimal self-built ECS.
/// - entities are dense ids (1..N), never reused in v1
/// - components live in per-type hash maps (sparse, fine for 5-50 entities)
/// - systems run in registration order, which maps 1:1 to tick phases
class Ecs {
public:
    Entity Create()
    {
        const Entity e = _nextEntity++;
        _alive[e] = true;
        return e;
    }

    void Destroy(Entity e)
    {
        _alive[e] = false;
        for (auto& [type, storage] : _stores)
            storage->Remove(e);
    }

    bool Alive(Entity e) const
    {
        auto it = _alive.find(e);
        return it != _alive.end() && it->second;
    }

    std::size_t Count() const
    {
        std::size_t n = 0;
        for (auto& [e, alive] : _alive)
            if (alive) ++n;
        return n;
    }

    template <typename T, typename... Args>
    T& Add(Entity e, Args&&... args)
    {
        auto& store = Store<T>();
        auto it = store.data.find(e);
        if (it == store.data.end()) {
            auto [inserted, ok] = store.data.emplace(e, T(std::forward<Args>(args)...));
            (void)ok;
            return inserted->second;
        }

        it->second = T(std::forward<Args>(args)...);
        return it->second;
    }

    template <typename T>
    T* Get(Entity e)
    {
        auto& store = Store<T>();
        auto it = store.data.find(e);
        return it == store.data.end() ? nullptr : &it->second;
    }

    template <typename T>
    const T* Get(Entity e) const
    {
        const auto& store = Store<T>();
        auto it = store.data.find(e);
        return it == store.data.end() ? nullptr : &it->second;
    }

    template <typename T>
    bool Has(Entity e) const
    {
        const auto& store = Store<T>();
        return store.data.find(e) != store.data.end();
    }

    template <typename T>
    void Remove(Entity e)
    {
        Store<T>().data.erase(e);
    }

    template <typename T>
    std::unordered_map<Entity, T>& All()
    {
        return Store<T>().data;
    }

    template <typename T>
    const std::unordered_map<Entity, T>& All() const
    {
        return Store<T>().data;
    }

    void AddSystem(std::function<void(Ecs&, double)> system)
    {
        _systems.push_back(std::move(system));
    }

    void Update(double dt)
    {
        for (auto& system : _systems)
            system(*this, dt);
    }

private:
    struct IStorage {
        virtual ~IStorage() = default;
        virtual void Remove(Entity e) = 0;
    };

    template <typename T>
    struct Storage final : IStorage {
        std::unordered_map<Entity, T> data;

        void Remove(Entity e) override { data.erase(e); }
    };

    template <typename T>
    Storage<T>& Store()
    {
        const std::type_index key(typeid(T));
        auto it = _stores.find(key);
        if (it == _stores.end()) {
            auto storage = std::make_unique<Storage<T>>();
            auto* raw = storage.get();
            _stores.emplace(key, std::move(storage));
            return *raw;
        }
        return static_cast<Storage<T>&>(*it->second);
    }

    template <typename T>
    const Storage<T>& Store() const
    {
        return const_cast<Ecs*>(this)->Store<T>();
    }

    std::uint32_t _nextEntity = 1;
    std::unordered_map<Entity, bool> _alive;
    std::unordered_map<std::type_index, std::unique_ptr<IStorage>> _stores;
    std::vector<std::function<void(Ecs&, double)>> _systems;
};

} // namespace coop
