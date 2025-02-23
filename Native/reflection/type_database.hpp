/**
 * \file reflection/type_database.hpp
 **/
#ifndef DOTOTHER_TYPE_DATABASE_HPP
#define DOTOTHER_TYPE_DATABASE_HPP

#include <string>
#include <vector>

#include <refl/refl.hpp>

namespace dotother {
  namespace echo {

    struct FieldMetadata {
      std::string name;
    };

    struct MethodMetadata {
      std::string name;
    };

    struct TypeMetadata {
      std::string name;
      std::vector<FieldMetadata> fields{};
      std::vector<MethodMetadata> methods{};

      template <typename T, typename... Flds>
      constexpr TypeMetadata(refl::type_descriptor<T> td)
          : name(td.name) {}
    };

    struct TypeDatabase {
     public:
      static TypeDatabase& Instance();
      static void CloseDatabase();

      template <typename T>
      const TypeMetadata& Get() {
        refl::type_descriptor<T> td = refl::reflect<T>();
        {
          auto itr = std::ranges::find_if(type_data, [&](auto& tmd) -> bool {
            if (tmd.name == std::string{ td.name.c_str() }) {
              return true;
            }
            return false;
          });

          if (itr != type_data.end()) {
            return *itr;
          }
        }

        auto& tmd = type_data.emplace_back(td);
        refl::util::for_each(td.members, [&](auto member) {
          std::string name = refl::descriptor::get_display_name(member);

          if constexpr (refl::descriptor::is_function(member)) {
            auto& minfo = tmd.methods.emplace_back();
            minfo.name = name;
            return;
          }

          if constexpr (refl::descriptor::is_field(member)) {
            auto& finfo = tmd.fields.emplace_back();
            finfo.name = name;
            return;
          }
        });

        return tmd;
      }

     private:
      static TypeDatabase* instance;

      std::vector<TypeMetadata> type_data;
    };

  }  // namespace echo
}  // namespace dotother

#endif  // !DOTOTHER_TYPE_DATABASE_HPP
