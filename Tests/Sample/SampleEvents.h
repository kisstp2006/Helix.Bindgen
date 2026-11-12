#pragma once

#include <Helix/Core/Containers/Array.h>

// Events of the sample module, with the members generated bindings use (the engine's events have the same ones):
// Add (an id), Remove by id or by callback and user, Invoke / Ask.
namespace helix::sample
{
    template <class R, class... Args>
    class Callbacks
    {
    public:
        using Callback = R (*)(void* user, Args... args);

        u32 Add(Callback callback, void* user = nullptr)
        {
            entries_.Push({callback, user, ++next_});
            return next_;
        }

        void Remove(u32 id)
        {
            for (usize i = 0; i < entries_.Size(); i++)
                if (entries_[i].id == id)
                {
                    entries_.RemoveAt(i);
                    return;
                }
        }

        void Remove(Callback callback, void* user)
        {
            for (usize i = 0; i < entries_.Size(); i++)
                if (entries_[i].callback == callback && entries_[i].user == user)
                {
                    entries_.RemoveAt(i);
                    return;
                }
        }

        usize Size() const { return entries_.Size(); }

    protected:
        struct Entry
        {
            Callback callback;
            void* user;
            u32 id;
        };
        Array<Entry> entries_;
        u32 next_ = 0;
    };

    template <class... Args>
    class Event : public Callbacks<void, Args...>
    {
    public:
        void Invoke(Args... args) const
        {
            auto now = this->entries_;
            for (const auto& e : now)
                e.callback(e.user, args...);
        }
    };

    template <class... Args>
    class Query : public Callbacks<bool, Args...>
    {
    public:
        bool Ask(Args... args) const
        {
            auto now = this->entries_;
            bool yes = true;
            for (const auto& e : now)
                yes = e.callback(e.user, args...) && yes;
            return yes;
        }
    };
}
