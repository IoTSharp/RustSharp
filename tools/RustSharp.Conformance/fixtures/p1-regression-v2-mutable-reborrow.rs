fn main() {
    let mut value = 1;
    let parent = &mut value;
    {
        let child = &mut *parent;
        *child = 9;
        println!("{}", *child);
    }
    println!("{}", *parent);
}
